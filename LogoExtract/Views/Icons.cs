namespace LogoExtract.Views;

/// <summary>Material Icons glyph constants for EditPage's icon-only controls -- same font/convention as
/// DocScanner's own Views/Icons.cs (MaterialIcons-Regular.ttf, registered under this family name in
/// MauiProgram.cs). Owner's report, 2026-10-27, "các icon xấu vậy. tham khảo các app khác": these replace the
/// raw emoji characters (✏️💾📤✓) that were used as a placeholder when EditPage's bottom toolbar was built.</summary>
public static class Icons
{
    public const string FontFamily = "Icons";

    public const string Edit = "";      // edit -- "Vector hoá"
    public const string Gallery = "";   // photo_library
    public const string Share = "";     // share -- "Chia sẻ / Xuất"
    public const string Checked = "";   // check_circle -- "Hoàn thành", "Xong"
    public const string Check = "";     // check -- "Lưu chỉnh màu", "Xác nhận vùng"
    public const string Save = "";      // save -- "Lưu vào thư viện ảnh"
    public const string Brush = "";     // brush -- "Thêm" (brush-add to mask)
    public const string Remove = "";    // remove -- "Xoá" (brush-subtract from mask)
    public const string CropFree = "";  // crop_free -- "Chọn vùng & tách lại"
    public const string Close = "";     // close -- "Huỷ"
    public const string Camera = "";    // photo_camera -- "Chụp ảnh"
    public const string Refresh = ""; // refresh -- "Tách lại nền" (HomePage's status-override button)
    public const string Tune = ""; // tune -- "Chỉnh sửa" (merged edit screen bottom-toolbar button that opens the brush sub-toolbar, 2026-10-29)

    // Settings/About/Privacy (added 2026-10-28). Codepoints copied verbatim from the identical Material Icons
    // font in the sibling PdfReader repo's own Views/Icons.cs (same MaterialIcons-Regular.ttf file) -- written
    // as \uXXXX escapes rather than pasted raw glyphs, since those PUA codepoints don't render as visible text
    // while editing/reviewing this file.
    public const string Settings = "";     // settings -- HomePage header gear icon
    public const string Info = "";         // info -- Settings' "Thông tin ứng dụng" row
    public const string Mail = "";         // email -- AboutPage contact row
    public const string Phone = "";        // phone -- AboutPage contact row
    public const string ChevronRight = ""; // chevron_right -- Settings/About tappable row trailing glyph
}
