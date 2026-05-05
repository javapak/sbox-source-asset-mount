using Sandbox.Mounting;
using Sledge.Formats.Texture.Vtf;

namespace Sandbox.Mounting.Source1;

class Source1TextureLoader : ResourceLoader<Source1Mount>
{
    #nullable enable
    private readonly string?       _path;

    public Source1TextureLoader( Source1Mount host, string path )        { _path  = path;  }

    protected override async Task<object> LoadAsync()
    {
        byte[] bytes = await File.ReadAllBytesAsync( _path! );

        using var stream = new MemoryStream( bytes );

    var vtf = new VtfFile( stream );
    var vtfImage = vtf.Images
        .OrderByDescending( x => x.Width )
        .ThenBy( x => x.Frame )
        .ThenBy( x => x.Face )
        .First();

    var rgba   = vtfImage.GetBgra32Data();
    int width  = vtfImage.Width;
    int height = vtfImage.Height;

    return Texture.Create( width, height )
        .WithData( rgba )
        .WithMips()
        .Finish();


    }
}