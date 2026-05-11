using Sandbox.Mounting;
using System;
using System.IO;
using System.Threading.Tasks;

namespace Sandbox.Mounting.Source1;

public class Source1Mount : BaseGameMount
{
    public override string Ident => "bladesymphony";
    public override string Title => "Blade Symphony";

    private const int AppId = 225600;

    internal string GameDir  { get; private set; }
    internal string BerimDir { get; private set; }



    protected override void Initialize( InitializeContext context )
    {
        if ( !context.IsAppInstalled( AppId ) )
            return;

            AppDomain.CurrentDomain.AssemblyResolve += ( sender, args ) =>
    {
        var name = new System.Reflection.AssemblyName( args.Name ).Name;
        var dir  = System.IO.Path.GetDirectoryName( typeof( Source1Mount ).Assembly.Location )!;
        var path = System.IO.Path.Combine( dir, name + ".dll" );
        if ( System.IO.File.Exists( path ) ) {
            Log.Info( $"Resolving assembly {args.Name} from {path}" );
            return System.Reflection.Assembly.LoadFile( path );
        }
        return null;
    };

        GameDir  = context.GetAppDirectory( AppId );
        BerimDir = System.IO.Path.Combine( GameDir, "berimbau" );

        if ( !System.IO.Directory.Exists( BerimDir ) )
            return;

        IsInstalled = true;
    }

    protected override Task Mount( MountContext context )
    {
        MountMaterials( context );
        MountModels( context );

        IsMounted = true;
        return Task.CompletedTask;
    }

    private void MountMaterials( MountContext context )
    {
        var materialsDir = System.IO.Path.Combine( BerimDir, "materials" );
        if ( !System.IO.Directory.Exists( materialsDir ) ) return;

        foreach ( var f in System.IO.Directory.EnumerateFiles( materialsDir, "*.vtf", SearchOption.AllDirectories ) )
        {
            var rel = System.IO.Path.ChangeExtension( Rel( f ), null );
            context.Add( ResourceType.Texture, rel, new VtfTextureLoader( f ) );
        }

        foreach ( var f in System.IO.Directory.EnumerateFiles( materialsDir, "*.vmt", SearchOption.AllDirectories ) )
        {
            var rel = System.IO.Path.ChangeExtension( Rel( f ), null );
            context.Add( ResourceType.Material, rel, new VmtMaterialLoader( f ) );
        }
    }

    private void MountModels( MountContext context )
    {
        var playersDir = System.IO.Path.Combine( BerimDir, "models", "player" );
        if ( !System.IO.Directory.Exists( playersDir ) ) return;

        foreach ( var f in System.IO.Directory.EnumerateFiles( playersDir, "*.mdl", SearchOption.AllDirectories ) )
            context.Add( ResourceType.Model, Rel( f ), new Source1ModelLoader( this, f ) );
    }

        #nullable enable
        internal byte[]? ReadCompanion( string mdlFullPath, string newExt )
        {
            var path = System.IO.Path.ChangeExtension( mdlFullPath, newExt );
            return System.IO.File.Exists( path ) ? System.IO.File.ReadAllBytes( path ) : null;
        }

        private string Rel( string full )
            => System.IO.Path.GetRelativePath( BerimDir, full ).Replace( '\\', '/' );
    }