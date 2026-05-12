using MdlCrowbar;
using Sandbox.Mounting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Sandbox.Mounting.Source1;

class Source1ModelLoader : ResourceLoader<Source1Mount>
{
    #nullable enable
    private readonly string? _path;

    public Source1ModelLoader( Source1Mount host, string path ) { _path = path; }

    protected override async Task<object> LoadAsync()
    {
        try {
        var mdlBytes = await File.ReadAllBytesAsync( _path! );

        // Parse MDL first to determine if it's anim-only
        var mdlData = new SourceMdlFileData49();
        using var mdlReader = new BinaryReader( new MemoryStream( mdlBytes ) );
        var mdlFile = new SourceMdlFile49( mdlReader, mdlData );
        mdlFile.ReadMdlHeader00();
        mdlFile.ReadMdlHeader01();
        mdlFile.ReadBones();
        mdlFile.ReadBodyParts();
        mdlFile.ReadTextures();
        mdlFile.ReadTexturePaths();
        mdlFile.ReadLocalAnimationDescs();
        mdlFile.ReadAnimationSections();
        mdlFile.ReadAnimationMdlBlocks();
        mdlFile.ReadSequenceDescs();



        var allAnimDescs = new List<SourceMdlAnimationDesc49>( mdlData.theAnimationDescs ?? new() );
        var allSeqDescs  = new List<SourceMdlSequenceDesc>( mdlData.theSequenceDescs ?? new() );
        var animDescBones = new Dictionary<SourceMdlAnimationDesc49, List<SourceMdlBone>>();


        if ( !mdlData.theMdlFileOnlyHasAnimations ) {

        var playerDir = System.IO.Path.Combine( Host.BerimDir, "models", "player" );

        // Shared anim MDLs loaded for every character
        var sharedAnimFiles = new[]
        {
            "anim_shared.mdl",
            "male_anims.mdl",
            "female_anim_shared.mdl",
            "player_anim_shared.mdl",
        };


// Character MDL anim descs use character bones
        if ( mdlData.theAnimationDescs != null )
            foreach ( var ad in mdlData.theAnimationDescs )
                animDescBones[ad] = mdlData.theBones;

        foreach ( var fileName in sharedAnimFiles )
        {
            var animPath = System.IO.Path.Combine( playerDir, fileName );
            TryLoadAnimMdl( animPath, allAnimDescs, allSeqDescs, animDescBones );
        }

        // Character-specific anim MDL by internal model name
        var charAnimPath = System.IO.Path.Combine( playerDir, $"anim_{mdlData.theModelName}.mdl" );
        TryLoadAnimMdl( charAnimPath, allAnimDescs, allSeqDescs, animDescBones );
        }


        var vvdBytes = Host.ReadCompanion( _path!, ".vvd" );
        var vtxBytes = Host.ReadCompanion( _path!, ".dx90.vtx" );

        // Anim-only MDL has no geometry companion files
        if ( vvdBytes == null || vtxBytes == null )
        {
            if ( mdlData.theMdlFileOnlyHasAnimations )
                return BuildAnimOnlyModel( mdlData );

            throw new FileNotFoundException( $"Missing companion files for {_path}" );
        }

        // Parse VVD
        var vvdData = new SourceVvdFileData04();
        using var vvdReader = new BinaryReader( new MemoryStream( vvdBytes ) );
        var vvdFile = new SourceVvdFile04( vvdReader, vvdData );
        vvdFile.ReadSourceVvdHeader();
        vvdFile.ReadVertexes();
        if ( vvdData.fixupCount > 0 )
            vvdFile.ReadFixups();

        var vertices = vvdData.fixupCount > 0
            ? vvdData.theFixedVertexesByLod[0]
            : vvdData.theVertexes;

        // Parse VTX
        var vtxData = new SourceVtxFileData07();
        using var vtxReader = new BinaryReader( new MemoryStream( vtxBytes ) );
        var vtxFile = new SourceVtxFile07( vtxReader, vtxData );
        vtxFile.ReadSourceVtxHeader();
        vtxFile.ReadSourceVtxBodyParts();

        Log.Info( $"Total sequences: {allSeqDescs.Count}" );
        Log.Info( $"Total anim descs: {allAnimDescs.Count}" );
        foreach ( var seq in allSeqDescs.Take( 50 ) )
        {
            if ( seq.theAnimDescIndexes != null && seq.theAnimDescIndexes.Count > 0 )
            {
                var animDesc = allAnimDescs[seq.theAnimDescIndexes[0]];
                Log.Info( $"seq='{seq.theName}' seqflags=0x{seq.flags:X8} animflags=0x{animDesc.flags:X8} frameCount={animDesc.frameCount} isLinked={animDesc.theAnimIsLinkedToSequence}" );
            }
        }

        return BuildModel( mdlData, vvdData, vtxData, vertices, allAnimDescs, allSeqDescs, animDescBones );        }
        catch ( Exception ex )
        {
            throw new Exception( $"Failed loading {_path}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}", ex );        
        }
    }

    private void TryLoadAnimMdl(
        string path,
        List<SourceMdlAnimationDesc49> allAnimDescs,
        List<SourceMdlSequenceDesc> allSeqDescs,
        Dictionary<SourceMdlAnimationDesc49, List<SourceMdlBone>> animDescBones )
    {
        if ( !System.IO.File.Exists( path ) ) return;

        var animMdlData = new SourceMdlFileData49();
        using var animReader = new BinaryReader( File.OpenRead( path ) );
        var animMdlFile = new SourceMdlFile49( animReader, animMdlData );
        animMdlFile.ReadMdlHeader00();
        animMdlFile.ReadMdlHeader01();
        animMdlFile.ReadBones();
        animMdlFile.ReadLocalAnimationDescs();
        animMdlFile.ReadAnimationSections();
        animMdlFile.ReadAnimationMdlBlocks();
        animMdlFile.ReadSequenceDescs();

        if ( animMdlData.theAnimationDescs == null || animMdlData.theSequenceDescs == null ) return;

        int animDescOffset = allAnimDescs.Count;

        // Store bone list for each anim desc from this file
        foreach ( var animDesc in animMdlData.theAnimationDescs )
            animDescBones[animDesc] = animMdlData.theBones;

        allAnimDescs.AddRange( animMdlData.theAnimationDescs );

        foreach ( var seq in animMdlData.theSequenceDescs )
        {
            if ( seq.theAnimDescIndexes != null )
                for ( int i = 0; i < seq.theAnimDescIndexes.Count; i++ )
                    seq.theAnimDescIndexes[i] = (short)( seq.theAnimDescIndexes[i] + animDescOffset );
            allSeqDescs.Add( seq );
        }
    }

    // ── Anim-only MDL ─────────────────────────────────────────────────────

    private object BuildAnimOnlyModel( SourceMdlFileData49 mdlData )
    {
        var modelBuilder    = new ModelBuilder();
        var boneTransforms  = BuildBoneTransforms( mdlData );
        AddBonesToBuilder( mdlData, boneTransforms, modelBuilder );
        AddAnimationsToBuilder( mdlData, boneTransforms, modelBuilder);
        modelBuilder.WithName( System.IO.Path.GetFileNameWithoutExtension( _path! ) );
        return modelBuilder.Create();
    }

    // ── Full model ────────────────────────────────────────────────────────

    private object BuildModel(
    SourceMdlFileData49 mdlData,
    SourceVvdFileData04 vvdData,
    SourceVtxFileData07 vtxData,
    List<SourceVertex> vertices,
    List<SourceMdlAnimationDesc49> allAnimDescs,
    List<SourceMdlSequenceDesc> allSeqDescs,
    Dictionary<SourceMdlAnimationDesc49, List<SourceMdlBone>>? animDescBones = null
     )
    {
        var modelBuilder   = new ModelBuilder();
        var boneTransforms = BuildBoneTransforms( mdlData );

        if ( mdlData.theBodyParts == null || mdlData.theBodyParts.Count == 0 )
            return modelBuilder.Create();



        // ── Bones──────────────────────────────────────────
        AddBonesToBuilder( mdlData, boneTransforms, modelBuilder );
        // ── Build meshes ──────────────────────────────────────────────────

        for ( int bpIdx = 0; bpIdx < mdlData.theBodyParts.Count; bpIdx++ )
        {
            var mdlBp = mdlData.theBodyParts[bpIdx];
            var vtxBp = vtxData.theVtxBodyParts[bpIdx];

            if ( mdlBp.theModels == null || mdlBp.theModels.Count == 0 ) continue;

            // Only model 0 from each body part
            var mdlModel = mdlBp.theModels[0];
            var vtxModel = vtxBp.theVtxModels[0];
            var vtxLod0  = vtxModel.theVtxModelLods[0];

            if ( mdlModel.theMeshes == null ) continue;

            for ( int meshIdx = 0; meshIdx < mdlModel.theMeshes.Count; meshIdx++ )
            {
                var mdlMesh = mdlModel.theMeshes[meshIdx];
                var vtxMesh = vtxLod0.theVtxMeshes[meshIdx];

                int vertexOffset = mdlModel.vertexOffset + mdlMesh.vertexIndexStart;

                var vertexList = new List<SkinnedVertex>();
                var indexList  = new List<int>();
                var remapTable = new Dictionary<int, int>();
                int newIdx     = 0;

            foreach ( var sg in vtxMesh.theVtxStripGroups )
            {
                foreach ( var strip in sg.theVtxStrips )
                {
                    const byte STRIP_IS_TRILIST  = 0x01;
                    const byte STRIP_IS_TRISTRIP = 0x02;

                    if ( (strip.flags & STRIP_IS_TRILIST) != 0 )
                    {
                        for ( int i = 0; i < strip.indexCount; i += 3 )
                        {
                            int base_ = strip.indexMeshIndex + i;
                            int id0 = sg.theVtxVertexes[sg.theVtxIndexes[base_    ]].originalMeshVertexIndex + vertexOffset;
                            int id1 = sg.theVtxVertexes[sg.theVtxIndexes[base_ + 1]].originalMeshVertexIndex + vertexOffset;
                            int id2 = sg.theVtxVertexes[sg.theVtxIndexes[base_ + 2]].originalMeshVertexIndex + vertexOffset;
                            // Swap 1 and 2 to flip winding from Source to sbox convention
                            AddVertex( id0, strip, vertices, boneTransforms, vertexList, indexList, remapTable, ref newIdx );
                            AddVertex( id2, strip, vertices, boneTransforms, vertexList, indexList, remapTable, ref newIdx );
                            AddVertex( id1, strip, vertices, boneTransforms, vertexList, indexList, remapTable, ref newIdx );
                        }
                    }
                    else if ( (strip.flags & STRIP_IS_TRISTRIP) != 0 )
                    {
                        for ( int i = 0; i < strip.indexCount - 2; i++ )
                        {
                            int base_ = strip.indexMeshIndex;
                            int id0 = sg.theVtxVertexes[sg.theVtxIndexes[base_ + i    ]].originalMeshVertexIndex + vertexOffset;
                            int id1 = sg.theVtxVertexes[sg.theVtxIndexes[base_ + i + 1]].originalMeshVertexIndex + vertexOffset;
                            int id2 = sg.theVtxVertexes[sg.theVtxIndexes[base_ + i + 2]].originalMeshVertexIndex + vertexOffset;
                            if ( id0 == id1 || id1 == id2 || id0 == id2 ) continue;
                            if ( ( i & 1 ) == 0 )
                            {
                                AddVertex( id0, strip, vertices, boneTransforms, vertexList, indexList, remapTable, ref newIdx );
                                AddVertex( id2, strip, vertices, boneTransforms, vertexList, indexList, remapTable, ref newIdx );
                                AddVertex( id1, strip, vertices, boneTransforms, vertexList, indexList, remapTable, ref newIdx );
                            }
                            else
                            {
                                AddVertex( id1, strip, vertices, boneTransforms, vertexList, indexList, remapTable, ref newIdx );
                                AddVertex( id2, strip, vertices, boneTransforms, vertexList, indexList, remapTable, ref newIdx );
                                AddVertex( id0, strip, vertices, boneTransforms, vertexList, indexList, remapTable, ref newIdx );
                            }
                        }
                    }
                }
            }

                if ( vertexList.Count == 0 || indexList.Count == 0 ) continue;

                var mesh = new Mesh( ResolveMaterial( mdlData, mdlMesh.materialIndex ) );
                mesh.CreateVertexBuffer<SkinnedVertex>( vertexList.Count, vertexList );
                mesh.CreateIndexBuffer( indexList.Count, indexList );
                mesh.Bounds = BBox.FromPoints( vertexList.Select( v => v.Position ) );
                modelBuilder.AddMesh( mesh );
            }
        }

        AddAnimationsToBuilder( mdlData, boneTransforms, modelBuilder, allAnimDescs, allSeqDescs, animDescBones );

        modelBuilder.WithName( System.IO.Path.GetFileNameWithoutExtension( _path! ) );
        return modelBuilder.Create();
    }

    // ── Bone helpers ──────────────────────────────────────────────────────

    private static Transform[] BuildBoneTransforms( SourceMdlFileData49 mdlData )
    {
        var boneTransforms = new Transform[mdlData.theBones?.Count ?? 0];
        if ( mdlData.theBones == null ) return boneTransforms;

        for ( int i = 0; i < mdlData.theBones.Count; i++ )
        {

            var bone = mdlData.theBones[i];
            var localTx = new Transform(
                new Vector3( (float)bone.position.x, (float)bone.position.y, (float)bone.position.z ),
                new Rotation { x = (float)bone.quat.x, y = (float)bone.quat.y, z = (float)bone.quat.z, w = (float)bone.quat.w }
            );
            boneTransforms[i] = bone.parentBoneIndex >= 0 && bone.parentBoneIndex < i
                ? boneTransforms[bone.parentBoneIndex].ToWorld( localTx )
                : localTx;
        }
        return boneTransforms;
    }

    private static void AddBonesToBuilder(
        SourceMdlFileData49 mdlData,
        Transform[] boneTransforms,
        ModelBuilder modelBuilder )
    {
        if ( mdlData.theBones == null ) return;

        for ( int i = 0; i < mdlData.theBones.Count; i++ )
        {
            var bone = mdlData.theBones[i];

            var name       = string.IsNullOrWhiteSpace( bone.theName ) ? $"bone_{i}" : bone.theName;
            var parentName = bone.parentBoneIndex >= 0 ? mdlData.theBones[bone.parentBoneIndex].theName : null;

            modelBuilder.AddBone( name, boneTransforms[i].Position, boneTransforms[i].Rotation, parentName );
        }

    }

    // ── Animation helpers ─────────────────────────────────────────────────

  private void AddAnimationsToBuilder(
    SourceMdlFileData49 mdlData,
    Transform[] boneTransforms,
    ModelBuilder modelBuilder,
    List<SourceMdlAnimationDesc49>? animDescs = null,
    List<SourceMdlSequenceDesc>? seqDescs = null,
    Dictionary<SourceMdlAnimationDesc49, List<SourceMdlBone>>? animDescBones = null )
    {
        var animations = animDescs ?? mdlData.theAnimationDescs;
        var sequences  = seqDescs  ?? mdlData.theSequenceDescs;

        if ( sequences == null || animations == null ) return;

        int boneCount = mdlData.theBones?.Count ?? 0;
        if ( boneCount == 0 ) return;

        foreach ( var seq in sequences )
        {
            if ( seq.theAnimDescIndexes == null || seq.theAnimDescIndexes.Count == 0 ) continue;

            int animDescIdx = seq.theAnimDescIndexes[0];
            if ( animDescIdx < 0 || animDescIdx >= animations.Count ) continue;

            var animDesc = animations[animDescIdx];
            if ( animDesc.frameCount <= 0 ) continue;

            // Get the bone list that was used when parsing this anim desc
            List<SourceMdlBone>? animBones = null;
            animDescBones?.TryGetValue( animDesc, out animBones );

            var animBuilder = modelBuilder.AddAnimation( seq.theName, (float)animDesc.fps );
            animBuilder.WithLooping( ( animDesc.flags & 0x00000001 ) != 0 );
            animBuilder.WithDelta( ( animDesc.flags & 0x00000004 ) != 0 );
            animBuilder.WithInterpolationDisabled( ( animDesc.flags & 0x00000002 ) != 0 );

            for ( int frameIdx = 0; frameIdx < animDesc.frameCount; frameIdx++ )
            {
                var frameTransforms = BuildFrameTransforms( mdlData, animDesc, frameIdx, animBones );
                animBuilder.AddFrame( frameTransforms.AsSpan() );
            }
        }
    }

    private static Transform[] BuildFrameTransforms(
        SourceMdlFileData49 mdlData,
        SourceMdlAnimationDesc49 animDesc,
        int frameIdx,
        List<SourceMdlBone>? animBones = null )
    {
        int boneCount = mdlData.theBones!.Count;

        // Build name→index map from character MDL bones
        var boneNameToIndex = new Dictionary<string, int>();
        for ( int i = 0; i < boneCount; i++ )
            boneNameToIndex[mdlData.theBones[i].theName] = i;

        // Use anim MDL bones if provided, otherwise use character MDL bones
        var sourceBones = animBones ?? mdlData.theBones;

        // Start with bind pose local transforms from character MDL
        var localTransforms = new Transform[boneCount];
// Start with bind pose local transforms using quat directly
        for ( int i = 0; i < boneCount; i++ )
        {
            var bone = mdlData.theBones[i];
            localTransforms[i] = new Transform(
                new Vector3( (float)bone.position.x, (float)bone.position.y, (float)bone.position.z ),
                new Rotation
                {
                    x = (float)bone.quat.x,
                    y = (float)bone.quat.y,
                    z = (float)bone.quat.z,
                    w = (float)bone.quat.w
                }
            );
        }
        // Determine which section contains this frame
        int sectionIdx    = 0;
        int localFrameIdx = frameIdx;

        if ( animDesc.sectionFrameCount > 0
            && animDesc.theSections != null
            && animDesc.theSectionsOfAnimations != null )
        {
            sectionIdx    = frameIdx / animDesc.sectionFrameCount;
            localFrameIdx = frameIdx % animDesc.sectionFrameCount;
            sectionIdx    = Math.Clamp( sectionIdx, 0, animDesc.theSectionsOfAnimations.Count - 1 );
        }

        // Apply animation overrides
        if ( animDesc.theSectionsOfAnimations != null
            && sectionIdx < animDesc.theSectionsOfAnimations.Count )
        {
            var sectionAnims = animDesc.theSectionsOfAnimations[sectionIdx];

            foreach ( var anim in sectionAnims )
            {
                // Map anim bone index to character bone index by name
                int animBoneIdx = anim.boneIndex;
                if ( animBoneIdx < 0 || animBoneIdx >= sourceBones.Count ) continue;

                var animBone = sourceBones[animBoneIdx];

                // Look up corresponding bone in character skeleton by name
                if ( !boneNameToIndex.TryGetValue( animBone.theName, out int charBoneIdx ) ) continue;

                var charBone = mdlData.theBones[charBoneIdx];

        // Position — matching Crowbar's CalcBonePosition logic
        Vector3 pos;
        if ( ( anim.flags & 0x01 ) != 0 && anim.thePos != null )
        {
            // Raw constant 48-bit position
            pos = DecodePos48( anim.thePos, charBone );
        }
        else if ( ( anim.flags & 0x04 ) != 0 && anim.thePosV != null )
        {
            // RLE animated position
            float px = anim.thePosV.animXValueOffset > 0
                ? GetAnimValue( anim.thePosV.theAnimXValues, localFrameIdx ) * (float)charBone.positionScale.x
                : 0f;
            float py = anim.thePosV.animYValueOffset > 0
                ? GetAnimValue( anim.thePosV.theAnimYValues, localFrameIdx ) * (float)charBone.positionScale.y
                : 0f;
            float pz = anim.thePosV.animZValueOffset > 0
                ? GetAnimValue( anim.thePosV.theAnimZValues, localFrameIdx ) * (float)charBone.positionScale.z
                : 0f;

            // Add bone rest position if not delta
            if ( ( anim.flags & 0x10 ) == 0 )
            {
                px += (float)charBone.position.x;
                py += (float)charBone.position.y;
                pz += (float)charBone.position.z;
            }

            pos = new Vector3( px, py, pz );
        }
        else if ( ( anim.flags & 0x10 ) != 0 )
        {
            // Delta — zero position
            pos = Vector3.Zero;
        }
        else
        {
            // No animation data — use bone rest position
            pos = new Vector3( (float)charBone.position.x, (float)charBone.position.y, (float)charBone.position.z );
        }
                // Rotation — matching Crowbar's CalcBoneRotation logic
                Rotation rot;
                if ( ( anim.flags & 0x02 ) != 0 && anim.theRot48bits != null )
                {
                    // Raw 48-bit constant rotation
                    rot = new Rotation
                    {
                        x = (float)anim.theRot48bits.x,
                        y = (float)anim.theRot48bits.y,
                        z = (float)anim.theRot48bits.z,
                        w = (float)anim.theRot48bits.w
                    };
                }
                else if ( ( anim.flags & 0x20 ) != 0 && anim.theRot64bits != null )
                {
                    // Raw 64-bit constant rotation
                    rot = new Rotation
                    {
                        x = (float)anim.theRot64bits.x,
                        y = (float)anim.theRot64bits.y,
                        z = (float)anim.theRot64bits.z,
                        w = (float)anim.theRot64bits.w
                    };
                }
                else if ( ( anim.flags & 0x08 ) != 0 && anim.theRotV != null )
                {
                    // RLE animated rotation
                    float rx = anim.theRotV.animXValueOffset > 0
                        ? GetAnimValue( anim.theRotV.theAnimXValues, localFrameIdx ) * (float)charBone.rotationScale.x
                        : 0f;
                    float ry = anim.theRotV.animYValueOffset > 0
                        ? GetAnimValue( anim.theRotV.theAnimYValues, localFrameIdx ) * (float)charBone.rotationScale.y
                        : 0f;
                    float rz = anim.theRotV.animZValueOffset > 0
                        ? GetAnimValue( anim.theRotV.theAnimZValues, localFrameIdx ) * (float)charBone.rotationScale.z
                        : 0f;

                    // Add bone rest rotation if not a delta animation
                    if ( ( anim.flags & 0x10 ) == 0 )
                    {
                        rx += (float)charBone.rotation.x;
                        ry += (float)charBone.rotation.y;
                        rz += (float)charBone.rotation.z;
                    }

                    rot = EulerToRotation( rx, ry, rz );
                }
                else if ( ( anim.flags & 0x10 ) != 0 )
                {
                    // Delta — zero rotation
                    rot = Rotation.Identity;
                }
                else
                {
                    // No animation data — use bone rest rotation
                    rot = EulerToRotation( (float)charBone.rotation.x, (float)charBone.rotation.y, (float)charBone.rotation.z );
                }

                localTransforms[charBoneIdx] = new Transform( pos, rot );
            }
        }

        return localTransforms;
    }

    private static float GetAnimValue( List<SourceMdlAnimationValue>? values, int frame )
    {
        if ( values == null || values.Count == 0 ) return 0f;

        try
        {
            int remaining = frame;
            int idx       = 0;

            while ( values[idx].total <= remaining )
            {
                remaining -= values[idx].total;
                idx       += values[idx].valid + 1;

                if ( idx >= values.Count || values[idx].total == 0 )
                    return 0f;
            }

            return values[idx].valid <= remaining
                ? values[idx + values[idx].valid].value
                : values[idx + remaining + 1].value;
        }
        catch
        {
            return 0f;
        }
    }

    private static Rotation DecodeRot48( SourceQuaternion48bits rot48 )
    {
        float x = ( rot48.theXInput / 32768f ) - 1f;
        float y = ( rot48.theYInput / 32768f ) - 1f;
        float z = ( ( rot48.theZWInput >> 1 ) / 16383f ) - 1f;
        float w = MathF.Sqrt( Math.Max( 0f, 1f - x*x - y*y - z*z ) );
        if ( ( rot48.theZWInput & 1 ) != 0 ) w = -w;
        return new Rotation { x = x, y = y, z = z, w = w };
    }

    private static Rotation DecodeRot64( SourceQuaternion64bits rot64 )
    {
        var bytes = rot64.theBytes;
        float x = BitConverter.ToInt16( bytes, 0 ) / 32767f;
        float y = BitConverter.ToInt16( bytes, 2 ) / 32767f;
        float z = BitConverter.ToInt16( bytes, 4 ) / 32767f;
        float w = BitConverter.ToInt16( bytes, 6 ) / 32767f;
        return new Rotation { x = x, y = y, z = z, w = w };
    }

    private static Vector3 DecodePos48( SourceVector48bits pos48, SourceMdlBone bone )
    {
        float x = (float)(( pos48.theXInput.the16BitValue / 32768f ) * bone.positionScale.x + bone.position.x);
        float y = (float)(( pos48.theYInput.the16BitValue / 32768f ) * bone.positionScale.y + bone.position.y);
        float z = (float)(( pos48.theZInput.the16BitValue / 32768f ) * bone.positionScale.z + bone.position.z);
        return new Vector3( x, y, z );
    }

    // ── Vertex helpers ────────────────────────────────────────────────────

    private static void AddVertex(
    int origVertId,
    SourceVtxStrip07 strip,
    List<SourceVertex> vertices,
    Transform[] boneTransforms,
    List<SkinnedVertex> vertexList,
    List<int> indexList,
    Dictionary<int, int> remapTable,
    ref int newIdx )
    {
        if ( remapTable.TryGetValue( origVertId, out var mapped ) )
        {
            indexList.Add( mapped );
            return;
        }

        if ( origVertId < 0 || origVertId >= vertices.Count ) return;

        var v  = vertices[origVertId];
        var bw = v.boneWeight;

        var pos    = new Vector3( (float)v.positionX, (float)v.positionY, (float)v.positionZ );
        var normal = new Vector3( (float)v.normalX, (float)v.normalY, (float)v.normalZ );

        if ( float.IsNaN( pos.x ) || float.IsNaN( pos.y ) || float.IsNaN( pos.z ) )
            pos = Vector3.Zero;

        int boneCount = bw.boneCount;
        byte b0 = boneCount > 0 ? ResolveBoneId( strip, bw.bone[0], boneTransforms.Length ) : (byte)0;
        byte b1 = boneCount > 1 ? ResolveBoneId( strip, bw.bone[1], boneTransforms.Length ) : (byte)0;
        byte b2 = boneCount > 2 ? ResolveBoneId( strip, bw.bone[2], boneTransforms.Length ) : (byte)0;

        float w0 = boneCount > 0 ? (float)bw.weight[0] : 1f;
        float w1 = boneCount > 1 ? (float)bw.weight[1] : 0f;
        float w2 = boneCount > 2 ? (float)bw.weight[2] : 0f;
        float sum = w0 + w1 + w2;
        if ( sum > 0.0001f ) { w0 /= sum; w1 /= sum; w2 /= sum; } else { w0 = 1f; }

        int iw0 = (int)(w0 * 255f + 0.5f);
        int iw1 = (int)(w1 * 255f + 0.5f);
        int iw2 = (int)(w2 * 255f + 0.5f);
        int weightSum = iw0 + iw1 + iw2;
        if ( weightSum != 255 )
        {
            int diff = 255 - weightSum;
            if ( iw0 >= iw1 && iw0 >= iw2 ) iw0 += diff;
            else if ( iw1 >= iw2 )           iw1 += diff;
            else                             iw2 += diff;
        }

        vertexList.Add( new SkinnedVertex
        {
            Position     = pos,
            Normal       = normal,
            TexCoord     = new Vector2( (float)v.texCoordX, (float)v.texCoordY ),
            BlendIndices = new Color32( b0, b1, b2, 0 ),
            BlendWeights = new Color32( (byte)iw0, (byte)iw1, (byte)iw2, 0 ),
        } );

        remapTable[origVertId] = newIdx;
        indexList.Add( newIdx );
        newIdx++;
    }

    private static byte ResolveBoneId( SourceVtxStrip07 strip, int hwBoneId, int maxBoneCount )
    {
        if ( strip.theVtxBoneStateChanges != null )
            foreach ( var bsc in strip.theVtxBoneStateChanges )
                if ( bsc.hardwareId == hwBoneId )
                    return (byte)Math.Clamp( bsc.newBoneId, 0, maxBoneCount - 1 );
        return (byte)Math.Clamp( hwBoneId, 0, maxBoneCount - 1 );
    }

    // ── Material resolution ───────────────────────────────────────────────

    private Material ResolveMaterial( SourceMdlFileData49 mdlData, int materialIndex )
    {
        if ( mdlData.theTextures == null || materialIndex >= mdlData.theTextures.Count )
            return Material.Load( "materials/dev/dev_measuregeneric01.vmat" );

        var texName = mdlData.theTextures[materialIndex].thePathFileName;

        if ( mdlData.theTexturePaths != null )
        {
            foreach ( var dir in mdlData.theTexturePaths )
            {
                var matPath = $"mount://{Host.Ident}/materials/{dir.TrimEnd( '/' )}/{texName}.vmat"
                    .Replace( '\\', '/' )
                    .ToLowerInvariant();

                var mat = Material.Load( matPath );
                if ( mat != null ) return mat;
            }
        }

        return Material.Load( "materials/dev/dev_measuregeneric01.vmat" );
    }

    // ── Math helpers ──────────────────────────────────────────────────────

    private static Rotation EulerToRotation( float x, float y, float z )
    {
        var (sy, cy) = MathF.SinCos( z * 0.5f );
        var (sp, cp) = MathF.SinCos( y * 0.5f );
        var (sr, cr) = MathF.SinCos( x * 0.5f );

        return new Rotation
        {
            x = (sr * cp * cy) - (cr * sp * sy),
            y = (cr * sp * cy) + (sr * cp * sy),
            z = (cr * cp * sy) - (sr * sp * cy),
            w = (cr * cp * cy) + (sr * sp * sy)
        };
    }

    // ── Vertex struct ─────────────────────────────────────────────────────

    [StructLayout( LayoutKind.Sequential, Pack = 1 )]
    private struct SkinnedVertex
    {
        [VertexLayout.Position]
        public Vector3 Position;

        [VertexLayout.Normal]
        public Vector3 Normal;

        [VertexLayout.TexCoord]
        public Vector2 TexCoord;

        [VertexLayout.BlendIndices]
        public Color32 BlendIndices;

        [VertexLayout.BlendWeight]
        public Color32 BlendWeights;
    }
}