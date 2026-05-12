using System;
using System.IO;

namespace Sandbox.Mounting.Source1;

// Loads a Valve Texture Format (.vtf) file and produces a Sandbox Texture.
// Supports v7.0–7.5. DXT1/3/5 are reconstructed as a DDS blob and handed to
// TextureLoader.FromDds; uncompressed formats are decoded to RGBA8888 inline.
class VtfTextureLoader( string fullPath ) : ResourceLoader<Source1Mount>
{
    private enum VtfFormat : int
    {
        None              = -1,
        RGBA8888          =  0,
        ABGR8888          =  1,
        RGB888            =  2,
        BGR888            =  3,
        RGB565            =  4,
        I8                =  5,
        IA88              =  6,
        P8                =  7,
        A8                =  8,
        RGB888_Bluescreen =  9,
        BGR888_Bluescreen = 10,
        ARGB8888          = 11,
        BGRA8888          = 12,
        DXT1              = 13,
        DXT3              = 14,
        DXT5              = 15,
        BGRX8888          = 16,
        BGR565            = 17,
        BGRX5551          = 18,
        BGRA4444          = 19,
        DXT1_OneBitAlpha  = 20,
        BGRA5551          = 21,
        UV88              = 22,
        UVWQ8888          = 23,
        RGBA16161616F     = 24,
        RGBA16161616      = 25,
        UVLX8888          = 26,
    }

    protected override object Load()
    {
        try
        {
            return LoadVtf( File.ReadAllBytes( fullPath ) );
        }
        catch ( Exception ex )
        {
            Log.Warning( $"VTF load failed for {fullPath}: {ex.Message}" );
            return null;
        }
    }

    private static Texture LoadVtf( byte[] data )
    {
        using var ms = new MemoryStream( data );
        using var br = new BinaryReader( ms );

        // Signature: "VTF\0"
        if ( br.ReadUInt32() != 0x00465456u ) return null;

        uint vMajor    = br.ReadUInt32();
        uint vMinor    = br.ReadUInt32();
        uint headerSize= br.ReadUInt32();

        int  width     = br.ReadUInt16();
        int  height    = br.ReadUInt16();
        /*flags*/       br.ReadUInt32();
        int  frames    = br.ReadUInt16();
        /*firstFrame*/  br.ReadUInt16();
        br.ReadBytes( 4  );  // padding
        br.ReadBytes( 12 );  // reflectivity
        br.ReadBytes( 4  );  // padding
        br.ReadSingle();     // bumpScale

        var  fmt      = (VtfFormat)br.ReadInt32();
        int  mipCount = br.ReadByte();

        var  lowResFmt = (VtfFormat)br.ReadInt32();
        int  lowResW   = br.ReadByte();
        int  lowResH   = br.ReadByte();

        if ( vMinor >= 2 )
            br.ReadUInt16(); // depth (unused; we only load 2-D textures)

        // v7.3+ resource table — find HIGH_RES_IMAGE offset
        int imageDataOffset = -1;
        if ( vMinor >= 3 )
        {
            br.ReadBytes( 3 );           // padding
            uint numRes = br.ReadUInt32();
            br.ReadBytes( 8 );           // padding

            for ( uint i = 0; i < numRes; i++ )
            {
                byte t0 = br.ReadByte();
                byte t1 = br.ReadByte();
                byte t2 = br.ReadByte();
                br.ReadByte();           // resource flags
                int  rd = br.ReadInt32();

                // HIGH_RES_IMAGE tag = 0x30, 0x00, 0x00
                if ( t0 == 0x30 && t1 == 0x00 && t2 == 0x00 )
                    imageDataOffset = rd;
            }
        }

        // Legacy layout: header bytes + low-res thumbnail
        if ( imageDataOffset < 0 )
        {
            imageDataOffset = (int)headerSize;
            if ( lowResFmt != VtfFormat.None && lowResW > 0 && lowResH > 0 )
                imageDataOffset += MipDataSize( lowResFmt, lowResW, lowResH );
        }

        if ( mipCount < 1 || width < 1 || height < 1 ) return null;

        frames = Math.Max( 1, frames );

        return IsDxt( fmt )
            ? LoadDxt( fmt, width, height, mipCount, frames, data, imageDataOffset )
            : LoadRaw( fmt, width, height, mipCount, frames, data, imageDataOffset );
    }

    // ── Compressed (DXT) path ─────────────────────────────────────────────

    private static bool IsDxt( VtfFormat f ) =>
        f is VtfFormat.DXT1 or VtfFormat.DXT1_OneBitAlpha or VtfFormat.DXT3 or VtfFormat.DXT5;

    // VTF stores mips smallest-first; DDS expects largest-first.
    // Reconstruct a valid DDS blob, reversing the mip order, then
    // delegate to TextureLoader.FromDds which handles the rest.
    private static Texture LoadDxt(
        VtfFormat fmt, int width, int height, int mipCount, int frames,
        byte[] data, int dataOffset )
    {
        var sizes   = new int[mipCount];
        var offsets = new int[mipCount]; // per-mip absolute file offset (frame 0 only)

        int cur = dataOffset;
        for ( int m = mipCount - 1; m >= 0; m-- )
        {
            int mipSize = MipDataSize( fmt, Math.Max( 1, width >> m ), Math.Max( 1, height >> m ) );
            sizes[m]   = mipSize;
            offsets[m] = cur;
            cur += mipSize * frames; // advance past all frames of this mip level
        }

        using var ms = new MemoryStream( 128 + cur - dataOffset );
        using var bw = new BinaryWriter( ms );

        WriteDdsHeader( bw, fmt, width, height, mipCount );

        for ( int m = 0; m < mipCount; m++ )        // largest → smallest
            bw.Write( data, offsets[m], sizes[m] );

        return TextureLoader.FromDds( ms.ToArray() );
    }

    // ── Uncompressed path ─────────────────────────────────────────────────

    private static Texture LoadRaw(
        VtfFormat fmt, int width, int height, int mipCount, int frames,
        byte[] data, int dataOffset )
    {
        // Skip smaller mips to reach the full-resolution image (mip 0 = last in VTF).
        int skipBytes = 0;
        for ( int m = mipCount - 1; m >= 1; m-- )
            skipBytes += MipDataSize( fmt, Math.Max( 1, width >> m ), Math.Max( 1, height >> m ) ) * frames;

        int fullSize = MipDataSize( fmt, width, height );
        int srcOff   = dataOffset + skipBytes;

        if ( srcOff + fullSize > data.Length ) return null;

        var raw  = data.AsSpan( srcOff, fullSize );
        var rgba = ConvertToRgba8888( fmt, raw, width, height );
        if ( rgba == null ) return null;

        return Texture.Create( width, height )
            .WithData( rgba )
            .WithMips()
            .WithStaticUsage()
            .Finish();
    }

    // ── DDS header writer ─────────────────────────────────────────────────

    private static void WriteDdsHeader( BinaryWriter bw, VtfFormat fmt, int width, int height, int mipCount )
    {
        string fourCC = fmt switch
        {
            VtfFormat.DXT1 or VtfFormat.DXT1_OneBitAlpha => "DXT1",
            VtfFormat.DXT3 => "DXT3",
            _              => "DXT5",
        };

        // Magic + DDS_HEADER (124 bytes) = 128 bytes total before image data.
        bw.Write( 0x20534444u );   // 'DDS '
        bw.Write( 124u );          // DDS_HEADER.dwSize

        // dwFlags: CAPS | HEIGHT | WIDTH | PIXELFORMAT | MIPMAPCOUNT | LINEARSIZE
        bw.Write( 0x1u | 0x2u | 0x4u | 0x1000u | 0x20000u | 0x80000u );
        bw.Write( (uint)height );
        bw.Write( (uint)width );
        bw.Write( (uint)MipDataSize( fmt, width, height ) ); // dwPitchOrLinearSize
        bw.Write( 0u );            // dwDepth
        bw.Write( (uint)mipCount );
        bw.Write( new byte[44] );  // dwReserved1[11]

        // DDS_PIXELFORMAT (32 bytes)
        bw.Write( 32u );
        bw.Write( 0x4u );          // dwFlags: DDPF_FOURCC
        foreach ( char c in fourCC )
            bw.Write( (byte)c );
        // dwRGBBitCount, dwRBitMask, dwGBitMask, dwBBitMask, dwABitMask
        bw.Write( 0u ); bw.Write( 0u ); bw.Write( 0u ); bw.Write( 0u ); bw.Write( 0u );

        // dwCaps: COMPLEX | MIPMAP | TEXTURE
        bw.Write( 0x8u | 0x400000u | 0x1000u );
        bw.Write( 0u ); bw.Write( 0u ); bw.Write( 0u ); bw.Write( 0u ); // Caps2-4 + reserved
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static int MipDataSize( VtfFormat fmt, int w, int h ) => fmt switch
    {
        VtfFormat.DXT1 or VtfFormat.DXT1_OneBitAlpha =>
            Math.Max( 1, (w + 3) / 4 ) * Math.Max( 1, (h + 3) / 4 ) * 8,
        VtfFormat.DXT3 or VtfFormat.DXT5 =>
            Math.Max( 1, (w + 3) / 4 ) * Math.Max( 1, (h + 3) / 4 ) * 16,
        VtfFormat.RGBA8888 or VtfFormat.BGRA8888 or VtfFormat.ABGR8888 or
        VtfFormat.ARGB8888 or VtfFormat.BGRX8888 or
        VtfFormat.UVWQ8888 or VtfFormat.UVLX8888 => w * h * 4,
        VtfFormat.RGB888 or VtfFormat.BGR888 or
        VtfFormat.RGB888_Bluescreen or VtfFormat.BGR888_Bluescreen => w * h * 3,
        VtfFormat.BGR565 or VtfFormat.RGB565 or VtfFormat.IA88 or
        VtfFormat.BGRX5551 or VtfFormat.BGRA4444 or
        VtfFormat.BGRA5551 or VtfFormat.UV88 => w * h * 2,
        VtfFormat.I8 or VtfFormat.P8 or VtfFormat.A8 => w * h,
        VtfFormat.RGBA16161616 or VtfFormat.RGBA16161616F => w * h * 8,
        _ => 0,
    };
    
    #nullable enable
    private static byte[]? ConvertToRgba8888( VtfFormat fmt, ReadOnlySpan<byte> src, int w, int h )
    {
        int count = w * h;
        var dst   = new byte[count * 4];

        switch ( fmt )
        {
            case VtfFormat.RGBA8888:
                src[..dst.Length].CopyTo( dst );
                break;

            case VtfFormat.BGRA8888:
            case VtfFormat.BGRX8888:
                for ( int i = 0; i < count; i++ )
                {
                    dst[i*4  ] = src[i*4+2];
                    dst[i*4+1] = src[i*4+1];
                    dst[i*4+2] = src[i*4  ];
                    dst[i*4+3] = fmt == VtfFormat.BGRX8888 ? (byte)255 : src[i*4+3];
                }
                break;

            case VtfFormat.ABGR8888:
                for ( int i = 0; i < count; i++ )
                {
                    dst[i*4  ] = src[i*4+3];
                    dst[i*4+1] = src[i*4+2];
                    dst[i*4+2] = src[i*4+1];
                    dst[i*4+3] = src[i*4  ];
                }
                break;

            case VtfFormat.ARGB8888:
                for ( int i = 0; i < count; i++ )
                {
                    dst[i*4  ] = src[i*4+1];
                    dst[i*4+1] = src[i*4+2];
                    dst[i*4+2] = src[i*4+3];
                    dst[i*4+3] = src[i*4  ];
                }
                break;

            case VtfFormat.RGB888:
            case VtfFormat.RGB888_Bluescreen:
                for ( int i = 0; i < count; i++ )
                {
                    dst[i*4  ] = src[i*3  ];
                    dst[i*4+1] = src[i*3+1];
                    dst[i*4+2] = src[i*3+2];
                    dst[i*4+3] = 255;
                }
                break;

            case VtfFormat.BGR888:
            case VtfFormat.BGR888_Bluescreen:
                for ( int i = 0; i < count; i++ )
                {
                    dst[i*4  ] = src[i*3+2];
                    dst[i*4+1] = src[i*3+1];
                    dst[i*4+2] = src[i*3  ];
                    dst[i*4+3] = 255;
                }
                break;

            case VtfFormat.I8:
                for ( int i = 0; i < count; i++ )
                {
                    dst[i*4] = dst[i*4+1] = dst[i*4+2] = src[i];
                    dst[i*4+3] = 255;
                }
                break;

            case VtfFormat.IA88:
                for ( int i = 0; i < count; i++ )
                {
                    dst[i*4] = dst[i*4+1] = dst[i*4+2] = src[i*2];
                    dst[i*4+3] = src[i*2+1];
                }
                break;

            case VtfFormat.A8:
                for ( int i = 0; i < count; i++ )
                {
                    dst[i*4] = dst[i*4+1] = dst[i*4+2] = 255;
                    dst[i*4+3] = src[i];
                }
                break;

            default:
                return null;
        }

        return dst;
    }
}
