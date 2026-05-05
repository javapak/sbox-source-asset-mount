using Sandbox.Mounting;
using System.IO;
using ValveKeyValue;

namespace Sandbox.Mounting.Source1;

class Source1MaterialLoader : ResourceLoader<Source1Mount> {
    #nullable enable
    private readonly string?       _path;

   
    public Source1MaterialLoader( Source1Mount host, string path )        { _path  = path;  }

    protected override async Task<object> LoadAsync()
    {
        return Material.Load( "materials/dev/dev_measuregeneric01.vmat" );

    }

    private static string NormalizePath( string p )
        => p.Replace( '\\', '/' ).ToLowerInvariant().TrimStart( '/' );
}