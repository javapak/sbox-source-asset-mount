using System;
using Sandbox.Mounting;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Sandbox.Mounting.Source1;

/// <summary>
/// Parses Source 1 MDL + VVD + VTX (CS:GO / Blade Symphony, studiohdr version 44-49)
/// All struct layouts derived from studio.h and optimize.h
/// </summary>
public class MdlFile
{
    // ── Parsed output ─────────────────────────────────────────────────────────

    public string Name { get; private set; }
    public string SurfaceProp { get; private set; }
    public string[] Textures { get; private set; }
    public string[] TextureDirs { get; private set; }
    public Bone[] Bones { get; private set; }
    public BodyPart[] BodyParts { get; private set; }

    // [lodLevel] → vertices
    public Vertex[][] Vertices { get; private set; }

    // [lodLevel] → meshes
    public MeshData[][] Meshes { get; private set; }

    public bool HasGeometry { get; private set; }

    // ── Public types ──────────────────────────────────────────────────────────

    public struct Vertex
    {
        public float[] Position;     // [x, y, z]
        public float[] Normal;       // [x, y, z]
        public float[] TexCoord;     // [u, v]
        public float[] BoneWeights;  // [w0, w1, w2]
        public byte[]  BoneIndices;  // [b0, b1, b2]
        public byte    NumBones;
    }

    public struct MeshData
    {
        public int   Material; // index into Textures[]
        public int[] Indices;  // flat triangle list into Vertices[lod]
    }

    public struct Bone
    {
        public string  Name;
        public int     Parent;    // -1 = root
        public float[] Position;  // [x, y, z]
        public float[] Rotation;  // [x, y, z, w] quaternion
    }

    public struct BodyPart
    {
        public string    Name;
        public MdlModel[] Models;
    }

    public struct MdlModel
    {
        public string    Name;
        public int       VertexIndex; // global offset into VVD for this model

        public MdlMesh[] Meshes;
    }

    public struct MdlMesh
    {
        public int Material;
        public int VertexOffset; // offset into global VVD vertex array
        public int NumVertices;
    }

    // ── MDL header offsets (from studiohdr_t in studio.js) ───────────────────
    //
    // INT id(4) + INT version(4) + INT checksum(4) + STRING(64) name(64) +
    // INT dataLength(4) = 80 bytes to eyeposition
    // then 6x Vector (eyepos, illum, hull_min, hull_max, view_bbmin, view_bbmax) = 72
    // then INT flags(4) = 4
    // Total before bone_count = 156

    private const int OFF_CHECKSUM         = 8;
    private const int OFF_NAME             = 12;  // 64 bytes fixed string
    private const int OFF_BONE_COUNT       = 156;
    private const int OFF_BONE_INDEX       = 160;
    private const int OFF_SURFACEPROP_IDX  = 308;

    private const int OFF_TEXTURE_COUNT    = 204;
    private const int OFF_TEXTURE_INDEX    = 208;
    private const int OFF_TEXTUREDIR_COUNT = 212;
    private const int OFF_TEXTUREDIR_INDEX = 216;
    private const int OFF_SKINREF_COUNT    = 220;
    private const int OFF_SKINFAMILY_COUNT = 224;
    private const int OFF_SKINREF_INDEX    = 228;
    private const int OFF_BODYPART_COUNT   = 232;
    private const int OFF_BODYPART_INDEX   = 236;

    // ── Entry point ───────────────────────────────────────────────────────────

    public static MdlFile Load( byte[] mdl, byte[] vvd, byte[] vtx )
    
    {
        var f = new MdlFile();
        f.Parse( mdl, vvd, vtx );
        return f;
    }

private void Parse( byte[] mdl, byte[] vvd, byte[] vtx )
{
    if ( mdl.Length < 4 )
        throw new InvalidDataException( $"MDL too small: {mdl.Length}" );

    int id = ReadInt( mdl, 0 );
    if ( id != 0x54534449 )
        throw new InvalidDataException( $"Bad MDL magic: 0x{id:X8}" );

    int mdlVersion = ReadInt( mdl, 4 );
    if ( mdlVersion < 44 || mdlVersion > 49 )
        throw new InvalidDataException( $"Unsupported MDL version {mdlVersion}" );

    int mdlChecksum = ReadInt( mdl, OFF_CHECKSUM );

    if ( vvd != null )
    {
        int vvdChecksum = ReadInt( vvd, 8 );
        if ( vvdChecksum != mdlChecksum )
            throw new InvalidDataException( "VVD checksum mismatch" );
    }

    if ( vtx != null )
    {
        int vtxVersion = ReadInt( vtx, 0 );
        if ( vtxVersion != 7 )
            throw new InvalidDataException( $"Unsupported VTX version {vtxVersion}" );

        int vtxChecksum = ReadInt( vtx, 16 );
        if ( vtxChecksum != mdlChecksum )
            throw new InvalidDataException( "VTX checksum mismatch" );
    }

    // ── MDL: name + surfaceprop ───────────────────────────────────────────
    Name = ReadFixedString( mdl, OFF_NAME, 64 );

    int surfacePropIdx = ReadInt( mdl, OFF_SURFACEPROP_IDX );
    SurfaceProp = ReadNullTermString( mdl, surfacePropIdx );

    // ── MDL: textures ─────────────────────────────────────────────────────
    int textureCount  = ReadInt( mdl, OFF_TEXTURE_COUNT );
    int textureOffset = ReadInt( mdl, OFF_TEXTURE_INDEX );
    Textures = new string[textureCount];
    for ( int i = 0; i < textureCount; i++ )
    {
        int structBase = textureOffset + i * 64;
        int nameOffset = ReadInt( mdl, structBase );
        Textures[i] = ReadNullTermString( mdl, structBase + nameOffset );
    }

    // ── MDL: texture dirs ─────────────────────────────────────────────────
    int textureDirCount  = ReadInt( mdl, OFF_TEXTUREDIR_COUNT );
    int textureDirOffset = ReadInt( mdl, OFF_TEXTUREDIR_INDEX );
    TextureDirs = new string[textureDirCount];
    for ( int i = 0; i < textureDirCount; i++ )
    {
        int structBase = textureDirOffset + i * 4;
        int nameOffset = ReadInt( mdl, structBase );
        TextureDirs[i] = ReadNullTermString( mdl, structBase + nameOffset );
    }

    // ── MDL: bones ────────────────────────────────────────────────────────
    // mstudiobone_t layout (from studio.js):
    //   INT sznameindex(4) + 6x INT bonecontroller(24) = 28 bytes
    //   Vector pos(12) = offset 28
    //   Quaternion quat(16) = offset 40  ← may be garbage in some models
    //   Vector rot(12) = offset 56       ← euler angles, always valid
    //   Vector posscale(12) = offset 68
    //   Vector rotscale(12) = offset 80
    //   matrix3x4(48) = offset 92
    //   Quaternion qAlignment(16) = offset 140
    //   INT flags(4) = offset 156
    //   INT proctype(4) = offset 160
    //   INT procindex(4) = offset 164
    //   INT physicsbone(4) = offset 168
    //   INT surfacepropidx(4) = offset 172
    //   INT contents(4) = offset 176
    //   SKIP(32) = offset 180
    //   Total = 212 bytes
    const int BONE_STRUCT_SIZE = 212;
    int boneCount  = ReadInt( mdl, OFF_BONE_COUNT );
    int boneOffset = ReadInt( mdl, OFF_BONE_INDEX );
    Bones = new Bone[boneCount];
    for ( int i = 0; i < boneCount; i++ )
    {
        int b       = boneOffset + i * BONE_STRUCT_SIZE;
        int nameOff = ReadInt( mdl, b );
        int parent  = ReadInt( mdl, b + 4 );

        // pos at offset 28
        float px = ReadFloat( mdl, b + 28 );
        float py = ReadFloat( mdl, b + 32 );
        float pz = ReadFloat( mdl, b + 36 );

        // rot (euler angles) at offset 56 — more reliable than quat
        float rx = ReadFloat( mdl, b + 56 );
        float ry = ReadFloat( mdl, b + 60 );
        float rz = ReadFloat( mdl, b + 64 );

        Bones[i] = new Bone
        {
            Name     = ReadNullTermString( mdl, b + nameOff ),
            Parent   = parent,
            Position = new[] { px, py, pz },
            Rotation = new[] { rx, ry, rz, 0f }, // euler angles, converted in loader
        };
    }

    // ── MDL: body parts → models → meshes ────────────────────────────────
    const int BODYPART_STRUCT_SIZE = 16;
    const int MODEL_STRUCT_SIZE    = 148;
    const int MESH_STRUCT_SIZE     = 116;

    int bodyPartCount  = ReadInt( mdl, OFF_BODYPART_COUNT );
    int bodyPartOffset = ReadInt( mdl, OFF_BODYPART_INDEX );
    BodyParts = new BodyPart[bodyPartCount];

    for ( int i = 0; i < bodyPartCount; i++ )
    {
        int bpBase     = bodyPartOffset + i * BODYPART_STRUCT_SIZE;
        int nameOff    = ReadInt( mdl, bpBase );
        int numModels  = ReadInt( mdl, bpBase + 4 );
        int modelIndex = ReadInt( mdl, bpBase + 12 );

        var models = new MdlModel[numModels];
        for ( int j = 0; j < numModels; j++ )
        {
            int mBase       = bpBase + modelIndex + j * MODEL_STRUCT_SIZE;
            int numMeshes   = ReadInt( mdl, mBase + 68 );
            int meshIndex   = ReadInt( mdl, mBase + 72 );
            int vertexIndex = ReadInt( mdl, mBase + 76 );

            var meshes = new MdlMesh[numMeshes];
            for ( int k = 0; k < numMeshes; k++ )
            {
                int meshBase   = mBase + meshIndex + k * MESH_STRUCT_SIZE;
                int material   = ReadInt( mdl, meshBase );
                int numVerts   = ReadInt( mdl, meshBase + 8 );
                int vertOffset = ReadInt( mdl, meshBase + 12 );

                meshes[k] = new MdlMesh
                {
                    Material     = material,
                    NumVertices  = numVerts,
                    VertexOffset = vertOffset,
                };
            }

            models[j] = new MdlModel
            {
                Name        = ReadFixedString( mdl, mBase, 64 ),
                VertexIndex = vertexIndex,
                Meshes      = meshes,
            };
        }

        BodyParts[i] = new BodyPart
        {
            Name   = ReadNullTermString( mdl, bpBase + nameOff ),
            Models = models,
        };
    }

    // ── VVD: vertices ─────────────────────────────────────────────────────
    if ( vvd == null || vtx == null )
    {
        HasGeometry = false;
        return;
    }

    HasGeometry = true;

    int numLODs         = ReadInt( vvd, 12 );
    int numFixups       = ReadInt( vvd, 48 );
    int fixupTableStart = ReadInt( vvd, 52 );
    int vertexDataStart = ReadInt( vvd, 56 );

    Vertices = new Vertex[numLODs][];
    for ( int lod = 0; lod < numLODs; lod++ )
        Vertices[lod] = new Vertex[ReadInt( vvd, 16 + lod * 4 )];

    if ( numFixups == 0 )
    {
        int numLOD0Verts = Vertices[0].Length;
        for ( int i = 0; i < numLOD0Verts; i++ )
            Vertices[0][i] = ReadVertex( vvd, vertexDataStart + i * 48 );
    }
    else
    {
        var lodCursors = new int[numLODs];
        for ( int f = 0; f < numFixups; f++ )
        {
            int fixBase     = fixupTableStart + f * 12;
            int fixLod      = ReadInt( vvd, fixBase );
            int srcVertexId = ReadInt( vvd, fixBase + 4 );
            int numVerts    = ReadInt( vvd, fixBase + 8 );

            for ( int i = 0; i < numVerts; i++ )
            {
                var vert = ReadVertex( vvd, vertexDataStart + (srcVertexId + i) * 48 );
                for ( int lod = fixLod; lod >= 0; lod-- )
                {
                    if ( lodCursors[lod] < Vertices[lod].Length )
                        Vertices[lod][lodCursors[lod]++] = vert;
                }
            }
        }
    }

    // ── VTX: build index lists ────────────────────────────────────────────
    int vtxNumBodyParts = ReadInt( vtx, 28 );
    int vtxBodyPartOff  = ReadInt( vtx, 32 );

    var meshLists = new List<MeshData>[numLODs];
    for ( int lod = 0; lod < numLODs; lod++ )
        meshLists[lod] = new List<MeshData>();

    for ( int bpIdx = 0; bpIdx < vtxNumBodyParts; bpIdx++ )
    {
        int bpBase      = vtxBodyPartOff + bpIdx * 8;
        int bpNumModels = ReadInt( vtx, bpBase );
        int bpModelOff  = ReadInt( vtx, bpBase + 4 );

        for ( int mIdx = 0; mIdx < bpNumModels; mIdx++ )
        {
            if ( mIdx > 0 ) continue;

            int mBase    = bpBase + bpModelOff + mIdx * 8;
            int mNumLODs = ReadInt( vtx, mBase );
            int mLodOff  = ReadInt( vtx, mBase + 4 );

            var mdlModel  = BodyParts[bpIdx].Models[mIdx];
            var mdlMeshes = mdlModel.Meshes;

            for ( int lod = 0; lod < mNumLODs && lod < numLODs; lod++ )
            {
                int lodBase    = mBase + mLodOff + lod * 12;
                int lodNumMesh = ReadInt( vtx, lodBase );
                int lodMeshOff = ReadInt( vtx, lodBase + 4 );

                for ( int meshIdx = 0; meshIdx < lodNumMesh; meshIdx++ )
                {
                    int meshBase = lodBase + lodMeshOff + meshIdx * 9;
                    int numSGs   = ReadInt( vtx, meshBase );
                    int sgOff    = ReadInt( vtx, meshBase + 4 );

                    int mdlVertexOffset = meshIdx < mdlMeshes.Length
                        ? mdlModel.VertexIndex + mdlMeshes[meshIdx].VertexOffset
                        : 0;
                    int material = meshIdx < mdlMeshes.Length
                        ? mdlMeshes[meshIdx].Material
                        : 0;

                    var indices = new List<int>();

                    for ( int sgIdx = 0; sgIdx < numSGs; sgIdx++ )
                    {
                        int sgBase      = meshBase + sgOff + sgIdx * 25;
                        int sgNumVerts  = ReadInt( vtx, sgBase );
                        int sgVertOff   = ReadInt( vtx, sgBase + 4 );
                        int sgNumIdx    = ReadInt( vtx, sgBase + 8 );
                        int sgIdxOff    = ReadInt( vtx, sgBase + 12 );
                        int sgNumStrips = ReadInt( vtx, sgBase + 16 );
                        int sgStripOff  = ReadInt( vtx, sgBase + 20 );

                        var sgVerts = new ushort[sgNumVerts];
                        for ( int v = 0; v < sgNumVerts; v++ )
                        {
                            int vBase  = sgBase + sgVertOff + v * 9;
                            sgVerts[v] = ReadUShort( vtx, vBase + 4 );
                        }

                        var sgIndices = new ushort[sgNumIdx];
                        for ( int idx = 0; idx < sgNumIdx; idx++ )
                            sgIndices[idx] = ReadUShort( vtx, sgBase + sgIdxOff + idx * 2 );

                        for ( int stripIdx = 0; stripIdx < sgNumStrips; stripIdx++ )
                        {
                            int  stripBase   = sgBase + sgStripOff + stripIdx * 27;
                            int  stripNumIdx = ReadInt( vtx, stripBase );
                            int  stripIdxOff = ReadInt( vtx, stripBase + 4 );
                            byte stripFlags  = vtx[stripBase + 18];

                            const byte STRIP_IS_TRILIST  = 0x01;
                            const byte STRIP_IS_TRISTRIP = 0x02;

                            if ( (stripFlags & STRIP_IS_TRILIST) != 0 )
                            {
                                for ( int i = 0; i < stripNumIdx; i++ )
                                {
                                    ushort vtxId      = sgIndices[stripIdxOff + i];
                                    ushort origVertId = sgVerts[vtxId];
                                    indices.Add( origVertId + mdlVertexOffset );
                                }
                            }
                            else if ( (stripFlags & STRIP_IS_TRISTRIP) != 0 )
                            {
                                for ( int i = stripNumIdx + stripIdxOff; i >= stripIdxOff + 2; i-- )
                                {
                                    indices.Add( sgVerts[sgIndices[i]]     + mdlVertexOffset );
                                    indices.Add( sgVerts[sgIndices[i - 2]] + mdlVertexOffset );
                                    indices.Add( sgVerts[sgIndices[i - 1]] + mdlVertexOffset );
                                }
                            }
                        }
                    }

                    meshLists[lod].Add( new MeshData
                    {
                        Material = material,
                        Indices  = indices.ToArray(),
                    } );
                }
            }
        }
    }

    Meshes = new MeshData[numLODs][];
    for ( int lod = 0; lod < numLODs; lod++ )
        Meshes[lod] = meshLists[lod].ToArray();
}

    // ── Binary helpers ────────────────────────────────────────────────────────

    private static Vertex ReadVertex( byte[] buf, int offset )
    {
        // mstudioboneweight_t: 3x FLOAT weights(12) + 3x BYTE indices(3) + BYTE numBones(1) = 16
        // Vector position(12) + Vector normal(12) + Vector2 texcoord(8) = 48 total
        return new Vertex
        {
            BoneWeights = new[] { ReadFloat( buf, offset ),      ReadFloat( buf, offset + 4 ),  ReadFloat( buf, offset + 8 ) },
            BoneIndices = new[] { buf[offset + 12],               buf[offset + 13],              buf[offset + 14] },
            NumBones    = buf[offset + 15],
            Position    = new[] { ReadFloat( buf, offset + 16 ), ReadFloat( buf, offset + 20 ), ReadFloat( buf, offset + 24 ) },
            Normal      = new[] { ReadFloat( buf, offset + 28 ), ReadFloat( buf, offset + 32 ), ReadFloat( buf, offset + 36 ) },
            TexCoord    = new[] { ReadFloat( buf, offset + 40 ), ReadFloat( buf, offset + 44 ) },
        };
    }

    private static int    ReadInt   ( byte[] b, int o ) => BitConverter.ToInt32( b, o );
    private static float  ReadFloat ( byte[] b, int o ) => BitConverter.ToSingle( b, o );
    private static ushort ReadUShort( byte[] b, int o ) => BitConverter.ToUInt16( b, o );

    private static string ReadFixedString( byte[] b, int offset, int length )
    {
        int end = offset;
        while ( end < offset + length && b[end] != 0 ) end++;
        return Encoding.ASCII.GetString( b, offset, end - offset );
    }

    private static string ReadNullTermString( byte[] b, int offset )
    {
        if ( offset <= 0 || offset >= b.Length ) return "";
        int end = offset;
        while ( end < b.Length && b[end] != 0 ) end++;
        return Encoding.ASCII.GetString( b, offset, end - offset );
    }
}
