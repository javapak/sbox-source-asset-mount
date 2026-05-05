using Sandbox.Mounting;
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

        GameDir  = context.GetAppDirectory( AppId );
        BerimDir = System.IO.Path.Combine( GameDir, "berimbau" );

        if ( !System.IO.Directory.Exists( BerimDir ) )
            return;

        IsInstalled = true;
    }

    protected override Task Mount( MountContext context )
    {
        var playersDir = System.IO.Path.Combine( BerimDir, "models", "player" );

        if ( !System.IO.Directory.Exists( playersDir ) )
        {
            IsMounted = true;
            return Task.CompletedTask;
        }

        foreach ( var f in System.IO.Directory.EnumerateFiles( playersDir, "*.mdl", SearchOption.AllDirectories ) )
            context.Add( ResourceType.Model, Rel( f ), new Source1ModelLoader( this, f ) );

        foreach ( var f in System.IO.Directory.EnumerateFiles( playersDir, "*.vtf", SearchOption.AllDirectories ) )
            context.Add( ResourceType.Texture, System.IO.Path.ChangeExtension( Rel( f ), ".vtex" ), new Source1TextureLoader( this, f ) );

        foreach ( var f in System.IO.Directory.EnumerateFiles( playersDir, "*.vmt", SearchOption.AllDirectories ) )
            context.Add( ResourceType.Material, System.IO.Path.ChangeExtension( Rel( f ), ".vmat" ), new Source1MaterialLoader( this, f ) );

        IsMounted = true;
        return Task.CompletedTask;
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