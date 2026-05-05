using Sandbox.Mounting;
using System;
using System.IO;
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

        // Use LOD 0 only for MVP
        if ( !mdl.HasGeometry || mdl.Meshes == null || mdl.Meshes.Length == 0 )
            return modelBuilder.Create();

        var lod0Meshes   = mdl.Meshes[0];
        var lod0Vertices = mdl.Vertices[0];

        foreach ( var meshData in lod0Meshes )
        {
            if ( meshData.Indices == null || meshData.Indices.Length == 0 )
                continue;

           var vb = new VertexBuffer();
        vb.Init( true );

        // First pass: add all vertices and build remap table
        var remapTable = new Dictionary<int, int>();
        int newIdx = 0;

        var usedIndices = new HashSet<int>( meshData.Indices );
        foreach ( var origIdx in usedIndices.Order() )
        {
            if ( origIdx < 0 || origIdx >= lod0Vertices.Length ) continue;
            var v = lod0Vertices[origIdx];

            vb.Add( new Vertex
            {
                Position = SwizzlePosition( v.Position ),
                Normal   = SwizzleNormal( v.Normal ),
                TexCoord0 = v.TexCoord[0],
                TexCoord1 = v.TexCoord[1],
            } );

            remapTable[origIdx] = newIdx++;
        }
 
        // Second pass: add indices
        foreach ( var i in meshData.Indices )
        {
            if ( i < 0 || i >= lod0Vertices.Length ) continue;
            if ( remapTable.TryGetValue( i, out var mapped ) )
                vb.AddRawIndex( mapped );
        }

        var mesh = new Mesh( ResolveMaterial( mdl, meshData.Material, relPath ) );
        mesh.CreateBuffers( vb );
        modelBuilder.AddMesh( mesh );
        }

    
        if ( mdl.Bones != null && mdl.Bones.Length > 0 )
        {
            foreach ( var bone in mdl.Bones )
            {
                string? parentName = (bone.Parent >= 0 && bone.Parent < mdl.Bones.Length) ? mdl.Bones[bone.Parent].Name : null;
                var pos = new Vector3( bone.Position[0], bone.Position[1], bone.Position[2] );
                var rot = EulerToRotation( bone.Rotation[0], bone.Rotation[1], bone.Rotation[2] );
                modelBuilder.AddBone( bone.Name, pos, rot, parentName );
            }
        }
        modelBuilder.WithName( System.IO.Path.GetFileNameWithoutExtension( relPath ) );

        return modelBuilder.Create();
    }

	private Rotation EulerToRotation( float v1, float v2, float v3 )
	{
        // Source 1 uses ZYX order for Euler angles
        var rotZ = Rotation.FromAxis( Vector3.Forward, v1 );
        var rotY = Rotation.FromAxis( Vector3.Up, v2 );
        var rotX = Rotation.FromAxis( Vector3.Right, v3 );

        // Need to return rotation object 
        return rotZ * rotY * rotX;


	}

	private Material ResolveMaterial( MdlFile mdl, int materialIndex, string mdlRelPath )
    {

        return Material.Load( "materials/dev/dev_measuregeneric01.vmat" );
    }

    // Source 1 → Source 2 coordinates are the same. 

    private static Vector3 SwizzlePosition( float[] p ) => new( p[0], p[1], p[2] );
    private static Vector3 SwizzleNormal  ( float[] n ) => new( n[0], n[1], n[2] );

    private static int ReadInt( byte[] data, int offset ) => System.BitConverter.ToInt32( data, offset );

    private async Task<byte[]> ReadBytesAsync()
    {

        return await File.ReadAllBytesAsync( _path! );
    }
}
