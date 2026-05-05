using Sandbox.Mounting;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Sandbox.Mounting.Source1;

class Source1ModelLoader : ResourceLoader<Source1Mount>
{
  #nullable enable
    private readonly string?       _path;  // loose file path (relative to berimbau/)

    public Source1ModelLoader( Source1Mount host, string path )        { _path  = path;  }

    protected override async Task<object> LoadAsync()
    {
        byte[] mdlBytes = await ReadBytesAsync();


        // Resolve relative path for companion file lookup
        string relPath =
             System.Uri.UnescapeDataString(
                new System.Uri( System.IO.Path.GetFullPath( Host.BerimDir ).TrimEnd( System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar ) + System.IO.Path.DirectorySeparatorChar )
                    .MakeRelativeUri( new System.Uri( System.IO.Path.GetFullPath( _path! ) ) )
                    .ToString()
              ).Replace( '\\', '/' );

        byte[]? vvdBytes = Host.ReadCompanion( _path!, ".vvd" );
        byte[]? vtxBytes = Host.ReadCompanion( _path!, ".dx90.vtx" );

        if ( vvdBytes == null || vtxBytes == null )
            throw new FileNotFoundException( $"Missing companion files for {relPath}" );
        Log.Info( $"MDL buffer size: {mdlBytes.Length}" );

        Log.Info( $"Version: {ReadInt( mdlBytes, 4 )}" );
        Log.Info( $"vvdBytes length: {vvdBytes?.Length ?? -1}" );
        Log.Info( $"vtxBytes length: {vtxBytes?.Length ?? -1}" );

        var mdl = MdlFile.Load( mdlBytes, vvdBytes, vtxBytes );


        return BuildModel( mdl, relPath );
    }

    private object BuildModel( MdlFile mdl, string relPath )
    {
        var modelBuilder = new ModelBuilder();

        // Skeleton — register bones with parent-relative rest pose transforms so
        // the engine can derive the inverse bind pose for skinning.
        if ( mdl.Bones != null && mdl.Bones.Length > 0 )
        {
            foreach ( var bone in mdl.Bones )
            {
                string? parentName = bone.Parent >= 0 ? mdl.Bones[bone.Parent].Name : null;
                var pos = new Vector3( bone.Position[0], bone.Position[1], bone.Position[2] );
                var rot = EulerToRotation( bone.Rotation[0], bone.Rotation[1], bone.Rotation[2] );
                modelBuilder.AddBone( bone.Name, pos, rot, parentName );
            }
        }

        // Use LOD 0 only for MVP
        if ( !mdl.HasGeometry || mdl.Meshes == null || mdl.Meshes.Length == 0 )
            return modelBuilder.Create();

        var lod0Meshes   = mdl.Meshes[0];
        var lod0Vertices = mdl.Vertices[0];

        foreach ( var meshData in lod0Meshes )
        {
            if ( meshData.Indices == null || meshData.Indices.Length == 0 )
                continue;

            // Build a deduplicated skinned vertex list and a remap table.
            var skinnedVerts = new List<SkinnedVertex>();
            var remapTable   = new Dictionary<int, int>();
            int newIdx       = 0;

            foreach ( var origIdx in new HashSet<int>( meshData.Indices ).Order() )
            {
                if ( origIdx < 0 || origIdx >= lod0Vertices.Length ) continue;
                var v = lod0Vertices[origIdx];

                // Pack up to 3 bone influences into Color32 (4-component byte).
                // VVD weights are floats 0–1; map to 0–255.
                byte bi0 = v.BoneIndices[0];
                byte bi1 = v.NumBones > 1 ? v.BoneIndices[1] : (byte)0;
                byte bi2 = v.NumBones > 2 ? v.BoneIndices[2] : (byte)0;

                byte bw0 = PackWeight( v.BoneWeights[0] );
                byte bw1 = v.NumBones > 1 ? PackWeight( v.BoneWeights[1] ) : (byte)0;
                byte bw2 = v.NumBones > 2 ? PackWeight( v.BoneWeights[2] ) : (byte)0;

                skinnedVerts.Add( new SkinnedVertex(
                    SwizzlePosition( v.Position ),
                    SwizzleNormal( v.Normal ),
                    new Vector2( v.TexCoord[0], v.TexCoord[1] ),
                    new Color32( bi0, bi1, bi2, 0 ),
                    new Color32( bw0, bw1, bw2, 0 )
                ) );

                remapTable[origIdx] = newIdx++;
            }

            var indices = new List<int>( meshData.Indices.Length );
            foreach ( var i in meshData.Indices )
            {
                if ( remapTable.TryGetValue( i, out var mapped ) )
                    indices.Add( mapped );
            }

            var mesh = new Mesh( ResolveMaterial( mdl, meshData.Material, relPath ) );
            mesh.CreateVertexBuffer( skinnedVerts.Count, skinnedVerts );
            mesh.CreateIndexBuffer( indices.Count, indices.ToArray() );
            mesh.Bounds = BBox.FromPoints( skinnedVerts.Select( x => x.Position ) );
            modelBuilder.AddMesh( mesh );
        }

        return modelBuilder.Create();
    }

    private Material ResolveMaterial( MdlFile mdl, int materialIndex, string mdlRelPath )
    {

        return Material.Load( "materials/dev/dev_measuregeneric01.vmat" );
    }

    // Source 1 → Source 2 coordinates are the same.

    private static Vector3 SwizzlePosition( float[] p ) => new( p[0], p[1], p[2] );
    private static Vector3 SwizzleNormal  ( float[] n ) => new( n[0], n[1], n[2] );

    // Convert Source 1 ZYX euler angles (radians, stored as pitch/yaw/roll) to
    // a quaternion using the same formula as the GoldSrc loader.
    private static Rotation EulerToRotation( float rx, float ry, float rz )
    {
        var (sy, cy) = MathF.SinCos( rz * 0.5f );
        var (sp, cp) = MathF.SinCos( ry * 0.5f );
        var (sr, cr) = MathF.SinCos( rx * 0.5f );
        return new Rotation
        {
            x = (sr * cp * cy) - (cr * sp * sy),
            y = (cr * sp * cy) + (sr * cp * sy),
            z = (cr * cp * sy) - (sr * sp * cy),
            w = (cr * cp * cy) + (sr * sp * sy),
        };
    }

    // Clamp a 0–1 float weight into the 0–255 byte range.
    private static byte PackWeight( float w )
        => (byte)System.Math.Clamp( (int)(w * 255f + 0.5f), 0, 255 );

    private static int ReadInt( byte[] data, int offset ) => System.BitConverter.ToInt32( data, offset );

    private async Task<byte[]> ReadBytesAsync()
    {

        return await File.ReadAllBytesAsync( _path! );
    }
}

[StructLayout( LayoutKind.Sequential )]
struct SkinnedVertex( Vector3 position, Vector3 normal, Vector2 texcoord, Color32 blendIndices, Color32 blendWeights )
{
    [VertexLayout.Position]     public Vector3 Position     = position;
    [VertexLayout.Normal]       public Vector3 Normal       = normal;
    [VertexLayout.TexCoord]     public Vector2 Texcoord     = texcoord;
    [VertexLayout.BlendIndices] public Color32 BlendIndices = blendIndices;
    [VertexLayout.BlendWeight]  public Color32 BlendWeights = blendWeights;
}
