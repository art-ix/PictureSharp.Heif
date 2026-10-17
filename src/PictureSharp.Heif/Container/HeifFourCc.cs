namespace PictureSharp.Formats.Heif.Container;

// Four-character codes of the HEIF boxes, items, properties, references and brands the plugin reads or writes, as
// big-endian integers.
internal static class HeifFourCc
{
    // Boxes (ISO/IEC 14496-12, 23008-12).
    public const uint Ftyp = 0x66747970; // ftyp
    public const uint Meta = 0x6d657461; // meta
    public const uint Moov = 0x6d6f6f76; // moov
    public const uint Mdat = 0x6d646174; // mdat
    public const uint Uuid = 0x75756964; // uuid
    public const uint Hdlr = 0x68646c72; // hdlr
    public const uint Pict = 0x70696374; // pict
    public const uint Pitm = 0x7069746d; // pitm
    public const uint Iinf = 0x69696e66; // iinf
    public const uint Infe = 0x696e6665; // infe
    public const uint Iloc = 0x696c6f63; // iloc
    public const uint Iref = 0x69726566; // iref
    public const uint Iprp = 0x69707270; // iprp
    public const uint Ipco = 0x6970636f; // ipco
    public const uint Ipma = 0x69706d61; // ipma
    public const uint Idat = 0x69646174; // idat

    // Item types.
    public const uint Hvc1 = 0x68766331; // hvc1
    public const uint Grid = 0x67726964; // grid
    public const uint Iden = 0x6964656e; // iden
    public const uint Tmap = 0x746d6170; // tmap
    public const uint Exif = 0x45786966; // Exif
    public const uint Mime = 0x6d696d65; // mime
    public const uint Uri = 0x75726920; // uri

    // Item properties and colour types.
    public const uint Ispe = 0x69737065; // ispe
    public const uint Pixi = 0x70697869; // pixi
    public const uint Irot = 0x69726f74; // irot
    public const uint Imir = 0x696d6972; // imir
    public const uint Clap = 0x636c6170; // clap
    public const uint AuxC = 0x61757843; // auxC
    public const uint Colr = 0x636f6c72; // colr
    public const uint HvcC = 0x68766343; // hvcC
    public const uint Nclx = 0x6e636c78; // nclx
    public const uint Prof = 0x70726f66; // prof
    public const uint RIcc = 0x72494343; // rICC

    // Item reference types.
    public const uint Dimg = 0x64696d67; // dimg
    public const uint Auxl = 0x6175786c; // auxl
    public const uint Thmb = 0x74686d62; // thmb
    public const uint Cdsc = 0x63647363; // cdsc
    public const uint Prem = 0x7072656d; // prem

    // Brands.
    public const uint Heic = 0x68656963; // heic
    public const uint Heix = 0x68656978; // heix
    public const uint Heim = 0x6865696d; // heim
    public const uint Heis = 0x68656973; // heis
    public const uint Hevc = 0x68657663; // hevc
    public const uint Hevx = 0x68657678; // hevx
    public const uint Avif = 0x61766966; // avif
    public const uint Avis = 0x61766973; // avis
    public const uint Mif1 = 0x6d696631; // mif1
    public const uint Msf1 = 0x6d736631; // msf1
}
