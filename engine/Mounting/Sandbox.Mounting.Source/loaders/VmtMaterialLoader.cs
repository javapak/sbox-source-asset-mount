using System;
using System.Collections.Generic;
using System.IO;

namespace Sandbox.Mounting.Source1;

// Loads a Valve Material (.vmt) file and produces a Sandbox Material.
// Parses the KeyValues text format, maps common Source Engine parameters
// ($basetexture, $bumpmap, $selfillummask) to sbox "complex" shader slots,
// and loads referenced textures via their registered mount:// paths.
class VmtMaterialLoader( string fullPath ) : ResourceLoader<Source1Mount>
{
    protected override object Load()
    {
        try
        {
            return LoadVmt( fullPath, Host.Ident );
        }
        catch ( Exception ex )
        {
            Log.Warning( $"VMT load failed for {fullPath}: {ex.Message}" );
            return null;
        }
    }

    private static Material LoadVmt( string path, string ident )
    {
        var text = File.ReadAllText( path );
        ParseKeyValues( text, out _, out var props );

        var material = Material.Create( path, "complex", anonymous: false );

        // Sensible defaults so unset slots don't cause black/missing surfaces.
        var flatNormal = Texture.Create( 1, 1 )
            .WithData( new byte[] { 128, 128, 255, 255 } )
            .Finish();

        material.Set( "g_tNormal",           flatNormal    );
        material.Set( "g_tRoughness",         Texture.White );
        material.Set( "g_tAmbientOcclusion",  Texture.White );
        material.Set( "g_tEmissive",          Texture.Black );

        if ( props.TryGetValue( "$basetexture", out var baseTex ) )
        {
            var tex = LoadVtex( ident, baseTex );
            if ( tex != null ) material.Set( "g_tColor", tex );
        }

        // $bumpmap takes precedence over $normalmap if both are present.
        var normalKey = props.ContainsKey( "$bumpmap" ) ? "$bumpmap" : "$normalmap";
        if ( props.TryGetValue( normalKey, out var normalTex ) )
        {
            var tex = LoadVtex( ident, normalTex );
            if ( tex != null ) material.Set( "g_tNormal", tex );
        }

        if ( props.TryGetValue( "$selfillummask", out var emissiveTex ) )
        {
            var tex = LoadVtex( ident, emissiveTex );
            if ( tex != null ) material.Set( "g_tEmissive", tex );
        }

        return material;
    }

    // Build a mount:// path for a VTF reference (relative to materials/).
    #nullable enable
    private static Texture? LoadVtex( string ident, string vtfRelPath )
    {
        var norm = vtfRelPath.Replace( '\\', '/' ).ToLowerInvariant().Trim( '/' );
        var url  = $"mount://{ident}/materials/{norm}.vtex";
        var tex  = Texture.Load( url, warnOnMissing: false );
        return tex == null || tex.IsError ? null : tex;
    }

    // ── KeyValues parser ──────────────────────────────────────────────────
    // Handles quoted and unquoted tokens, // comments, and nested blocks.
    // Only top-level key/value pairs (depth == 1) are collected.

    private static void ParseKeyValues(
        string text, out string shader, out Dictionary<string, string> props )
    {
        shader = string.Empty;
        props  = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );

        int i     = 0;
        int depth = 0;

        while ( i < text.Length )
        {
            SkipWS( text, ref i );
            if ( i >= text.Length ) break;

            char ch = text[i];

            if ( ch == '{' ) { depth++; i++; continue; }
            if ( ch == '}' ) { depth--; i++; continue; }

            string key = ReadToken( text, ref i );
            if ( key.Length == 0 ) { i++; continue; }

            if ( depth == 0 )
            {
                // Root token is the shader name — no value follows.
                shader = key;
                continue;
            }

            SkipWS( text, ref i );
            if ( i >= text.Length ) break;

            if ( text[i] == '{' )
            {
                // Sub-block (e.g. Proxies) — skip entirely.
                SkipBlock( text, ref i );
                continue;
            }

            string value = ReadToken( text, ref i );
            if ( depth == 1 && key.Length > 0 && value.Length > 0 )
                props[key] = value;
        }
    }

    private static void SkipWS( string s, ref int i )
    {
        while ( i < s.Length )
        {
            if ( char.IsWhiteSpace( s[i] ) ) { i++; continue; }
            // Line comment
            if ( i + 1 < s.Length && s[i] == '/' && s[i + 1] == '/' )
            {
                while ( i < s.Length && s[i] != '\n' ) i++;
                continue;
            }
            break;
        }
    }

    private static string ReadToken( string s, ref int i )
    {
        SkipWS( s, ref i );
        if ( i >= s.Length ) return string.Empty;

        if ( s[i] == '"' )
        {
            i++;
            int start = i;
            while ( i < s.Length && s[i] != '"' ) i++;
            var tok = s[start..i].Trim();
            if ( i < s.Length ) i++; // closing quote
            return tok;
        }

        // Unquoted token — ends at whitespace or { }
        int ustart = i;
        while ( i < s.Length && !char.IsWhiteSpace( s[i] )
                              && s[i] != '{' && s[i] != '}' ) i++;
        return s[ustart..i].Trim();
    }

    private static void SkipBlock( string s, ref int i )
    {
        int depth = 0;
        while ( i < s.Length )
        {
            if      ( s[i] == '{' ) depth++;
            else if ( s[i] == '}' ) { if ( --depth == 0 ) { i++; return; } }
            i++;
        }
    }
}
