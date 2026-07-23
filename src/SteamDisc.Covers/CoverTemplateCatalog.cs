namespace SteamDisc.Covers;

/// <summary>
/// The cover templates the Builder knows about: blank geometry it ships, the Steam Game
/// Covers layouts it recognises on import, and anything the user has imported.
/// </summary>
/// <remarks>
/// <para>
/// No third-party artwork is bundled or downloaded. What is encoded here is <em>geometry</em>:
/// page size, trim box, spine width and slot positions, measured from the published templates.
/// A user downloads a design themselves and imports it, at which point the matching geometry is
/// applied and the design is composited over their key art — frame, branding and legal text
/// intact, as its terms require.
/// </para>
/// <para>
/// Geometry is page-first: a real template is a full sheet at 300 DPI with the cover inset in
/// the middle, surrounded by registration marks and credits that are part of the sheet but not
/// part of the cover.
/// </para>
/// </remarks>
public static class CoverTemplateCatalog
{
    /// <summary>Standard print bleed for the blank templates.</summary>
    public const double DefaultBleed = 3.0;

    /// <summary>Resolution the Steam Game Covers templates are authored at.</summary>
    public const double SgcDpi = 300.0;

    private const string SgcAttribution = "steamgamecovers.com";

    private const string SgcTerms =
        "Steam Game Covers template. Its terms require that the Steam header and the website " +
        "and legal text are not erased, altered or covered up, and that the template is not " +
        "resized. SteamDisc composites your art underneath the design, which keeps all of it intact.";

    // ---------------------------------------------------------------------------------------
    // Steam Game Covers layouts.
    //
    // Every figure below was measured from the published 300 DPI PNGs rather than guessed:
    // the trim box from the printed rule, the spine from its guide pair. They are expressed in
    // millimetres so a user whose cases differ can copy a template and adjust it.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// DVD (Amaray) case wrap. The most common format among existing community covers, and
    /// therefore the default.
    /// </summary>
    public static CoverTemplate SgcDvdCase() => Wrap(
        id: "sgc-dvd",
        name: "Steam — DVD case wrap",
        media: CoverMedia.DvdCase,
        page: new SizeMm(279.4, 215.9),         // US Letter, landscape
        trimOrigin: new SizeMm(3.64, 17.91),
        trimSize: new SizeMm(272.0, 181.5),
        spineWidth: 14.0,
        spineFontSize: 9);

    /// <summary>Blu-ray case wrap.</summary>
    public static CoverTemplate SgcBluRayCase() => Wrap(
        id: "sgc-bluray",
        name: "Steam — Blu-ray case wrap",
        media: CoverMedia.BluRayCase,
        page: new SizeMm(279.4, 215.9),
        trimOrigin: new SizeMm(6.14, 33.40),
        trimSize: new SizeMm(267.0, 149.0),
        spineWidth: 11.0,
        spineFontSize: 8);

    /// <summary>Jewel case front booklet.</summary>
    public static CoverTemplate SgcJewelFront() => SinglePanel(
        id: "sgc-jewel-front",
        name: "Steam — jewel case front",
        media: CoverMedia.JewelCase,
        page: new SizeMm(152.4, 152.4),
        trimOrigin: new SizeMm(16.13, 22.69),
        trimSize: new SizeMm(120.1, 113.5),
        slotId: "front",
        slotLabel: "Front cover");

    /// <summary>Jewel case tray card, including its two 6.5 mm fold spines.</summary>
    public static CoverTemplate SgcJewelBack()
    {
        const double spine = 6.52;
        var trim = new RectMm(6.99, 23.50, 151.0, 118.0);

        var template = new CoverTemplate
        {
            Id = "sgc-jewel-back",
            Name = "Steam — jewel case tray card",
            Family = "Steam",
            Media = CoverMedia.JewelCase,
            Page = new SizeMm(165.1, 165.1),
            Trim = trim,
            Bleed = 2,
            DrawCropMarks = false,
            SourceDpi = SgcDpi,
            Author = SgcAttribution,
            Source = SgcAttribution,
            Terms = SgcTerms,
            Slots =
            {
                new CoverSlot
                {
                    Id = "spineLeft",
                    Label = "Left spine",
                    Bounds = new RectMm(trim.X, trim.Y, spine, trim.Height),
                    Rotation = 90,
                },
                new CoverSlot
                {
                    Id = "back",
                    Label = "Back",
                    Bounds = new RectMm(trim.X + spine, trim.Y, trim.Width - (spine * 2), trim.Height),
                    PreferredAspect = (trim.Width - (spine * 2)) / trim.Height,
                },
                new CoverSlot
                {
                    Id = "spineRight",
                    Label = "Right spine",
                    Bounds = new RectMm(trim.Right - spine, trim.Y, spine, trim.Height),
                    Rotation = 90,
                },
            },
        };

        return template;
    }

    /// <summary>
    /// Disc label. Guides measured from the template: 120 mm disc, 38 mm stacking ring,
    /// 15 mm centre hole. The printable inner diameter depends on the disc, so the default is
    /// the conservative one.
    /// </summary>
    public static CoverTemplate SgcDiscLabel()
    {
        const double page = 152.4;
        const double outer = 120.1;
        const double inner = 38.4;   // stacking ring; safe for standard printable discs
        var origin = (page - outer) / 2;

        return new CoverTemplate
        {
            Id = "sgc-disc",
            Name = "Steam — disc label",
            Family = "Steam",
            Media = CoverMedia.DiscLabel,
            Page = new SizeMm(page, page),
            Trim = new RectMm(origin, origin, outer, outer),
            Bleed = 1,
            DrawCropMarks = false,
            SourceDpi = SgcDpi,
            Author = SgcAttribution,
            Source = SgcAttribution,
            Terms = SgcTerms,
            Slots =
            {
                new CoverSlot
                {
                    Id = "label",
                    Label = "Disc face",
                    Bounds = new RectMm(origin, origin, outer, outer),
                    Fit = SlotFit.Cover,
                    Circular = true,
                    InnerDiameter = inner,
                    PreferredAspect = 1.0,
                },
            },
        };
    }

    /// <summary>DVD case inner insert.</summary>
    public static CoverTemplate SgcDvdInsert() => SinglePanel(
        id: "sgc-dvd-insert",
        name: "Steam — DVD case insert",
        media: CoverMedia.Insert,
        page: new SizeMm(152.4, 215.9),
        trimOrigin: new SizeMm(16.13, 25.83),
        trimSize: new SizeMm(120.1, 173.6),
        slotId: "insert",
        slotLabel: "Insert");

    /// <summary>Blu-ray case inner insert.</summary>
    public static CoverTemplate SgcBluRayInsert() => SinglePanel(
        id: "sgc-bluray-insert",
        name: "Steam — Blu-ray case insert",
        media: CoverMedia.Insert,
        page: new SizeMm(152.4, 215.9),
        trimOrigin: new SizeMm(18.67, 41.24),
        trimSize: new SizeMm(115.0, 141.2),
        slotId: "insert",
        slotLabel: "Insert");

    // ---------------------------------------------------------------------------------------
    // Blank templates: correct geometry, no design.
    // ---------------------------------------------------------------------------------------

    /// <summary>DVD keep case wrap: 273 × 183 mm with a 14 mm spine.</summary>
    public static CoverTemplate BlankDvdCase() => BlankWrap(
        "blank-dvd", "DVD case wrap (blank)", CoverMedia.DvdCase, new SizeMm(273, 183), 14, 9);

    /// <summary>Blu-ray keep case wrap: 267 × 149 mm with an 11 mm spine.</summary>
    public static CoverTemplate BlankBluRayCase() => BlankWrap(
        "blank-bluray", "Blu-ray case wrap (blank)", CoverMedia.BluRayCase, new SizeMm(267, 149), 11, 8);

    public static CoverTemplate BlankJewelFront() => BlankPanel(
        "blank-jewel-front", "Jewel case front (blank)", CoverMedia.JewelCase, new SizeMm(121, 120), "front");

    /// <summary>Disc label: 120 mm disc with a 38 mm inner limit.</summary>
    public static CoverTemplate BlankDiscLabel()
    {
        const double outer = 120.0;
        var template = BlankPanel(
            "blank-disc", "Disc label (blank)", CoverMedia.DiscLabel, new SizeMm(outer, outer), "label");

        template.Bleed = 1;
        template.Page = new SizeMm(outer + 2, outer + 2);
        template.Trim = new RectMm(1, 1, outer, outer);

        var slot = template.Slots[0];
        slot.Bounds = template.Trim;
        slot.Circular = true;
        slot.InnerDiameter = 38.0;
        slot.PreferredAspect = 1.0;

        return template;
    }

    /// <summary>Every template this build ships geometry for.</summary>
    public static IReadOnlyList<CoverTemplate> BuiltIn { get; } = new[]
    {
        SgcDvdCase(),
        SgcBluRayCase(),
        SgcJewelFront(),
        SgcJewelBack(),
        SgcDiscLabel(),
        SgcDvdInsert(),
        SgcBluRayInsert(),
        BlankDvdCase(),
        BlankBluRayCase(),
        BlankJewelFront(),
        BlankDiscLabel(),
    };

    /// <summary>Default template when the user expresses no preference.</summary>
    public const string DefaultTemplateId = "sgc-dvd";

    /// <summary>Default user template folder: <c>&lt;LocalAppData&gt;/SteamDisc/covers</c>.</summary>
    public static string DefaultUserTemplateDirectory
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(root))
            {
                root = Path.GetTempPath();
            }

            return Path.Combine(root, "SteamDisc", "covers");
        }
    }

    /// <summary>Built-in templates plus every one found under <paramref name="userDirectory"/>.</summary>
    public static IReadOnlyList<CoverTemplate> Discover(string? userDirectory = null)
    {
        var templates = new List<CoverTemplate>(BuiltIn);
        var directory = userDirectory ?? DefaultUserTemplateDirectory;

        if (!Directory.Exists(directory))
        {
            return templates;
        }

        foreach (var file in Directory.EnumerateFiles(directory, CoverTemplate.FileName, SearchOption.AllDirectories))
        {
            try
            {
                var loaded = CoverTemplate.Load(file);

                // An imported copy of a built-in layout replaces it, so the picker does not show
                // the same design twice once its artwork is available.
                templates.RemoveAll(t => string.Equals(t.Id, loaded.Id, StringComparison.OrdinalIgnoreCase));
                templates.Add(loaded);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
            {
                // An unreadable template must not hide the rest of the library.
            }
        }

        return templates;
    }

    public static CoverTemplate? Find(string id, string? userDirectory = null)
        => Discover(userDirectory).FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The built-in layout that matches a medium, for a sensible default.</summary>
    public static CoverTemplate ForMedia(CoverMedia media) => media switch
    {
        CoverMedia.DiscLabel => SgcDiscLabel(),
        CoverMedia.JewelCase => SgcJewelFront(),
        CoverMedia.BluRayCase => SgcBluRayCase(),
        CoverMedia.Insert => SgcDvdInsert(),
        _ => SgcDvdCase(),
    };

    /// <summary>
    /// A template for printing a finished cover somebody else already made: one full-bleed
    /// slot over the trim area, no overlay and no text. The site hosts a large gallery of
    /// completed covers, and those need correct print geometry rather than a layout.
    /// </summary>
    public static CoverTemplate ForFinishedCover(CoverMedia media)
    {
        var reference = ForMedia(media);

        return new CoverTemplate
        {
            Id = "finished-" + media.ToString().ToLowerInvariant(),
            Name = $"Finished cover ({media})",
            Family = "Finished",
            Media = media,
            Page = reference.Page,
            Trim = reference.Trim,
            Bleed = reference.Bleed,
            DrawCropMarks = true,
            SourceDpi = SgcDpi,
            Slots =
            {
                new CoverSlot
                {
                    Id = "cover",
                    Label = "Complete cover",
                    Bounds = reference.Trim,
                    Fit = SlotFit.Cover,
                    Circular = media == CoverMedia.DiscLabel,
                    InnerDiameter = media == CoverMedia.DiscLabel ? 38.0 : 0,
                    PreferredAspect = reference.Trim.AspectRatio,
                },
            },
        };
    }

    // ---------------------------------------------------------------------------------------

    private static CoverTemplate Wrap(
        string id,
        string name,
        CoverMedia media,
        SizeMm page,
        SizeMm trimOrigin,
        SizeMm trimSize,
        double spineWidth,
        double spineFontSize)
    {
        var trim = new RectMm(trimOrigin.Width, trimOrigin.Height, trimSize.Width, trimSize.Height);
        var panel = (trim.Width - spineWidth) / 2;
        var spineX = trim.X + panel;

        return new CoverTemplate
        {
            Id = id,
            Name = name,
            Family = "Steam",
            Media = media,
            Page = page,
            Trim = trim,
            Bleed = 2,
            // These sheets already carry registration marks; a second set would confuse a printer.
            DrawCropMarks = false,
            SourceDpi = SgcDpi,
            Author = SgcAttribution,
            Source = SgcAttribution,
            Terms = SgcTerms,
            Slots =
            {
                new CoverSlot
                {
                    Id = "back",
                    Label = "Back cover",
                    Bounds = new RectMm(trim.X, trim.Y, panel, trim.Height),
                    PreferredAspect = panel / trim.Height,
                },
                new CoverSlot
                {
                    Id = "spine",
                    Label = "Spine",
                    Bounds = new RectMm(spineX, trim.Y, spineWidth, trim.Height),
                    Rotation = 90,
                },
                new CoverSlot
                {
                    Id = "front",
                    Label = "Front cover",
                    Bounds = new RectMm(spineX + spineWidth, trim.Y, panel, trim.Height),
                    PreferredAspect = panel / trim.Height,
                },
            },
            TextFields =
            {
                new CoverTextField
                {
                    Id = "spineTitle",
                    Bounds = new RectMm(spineX, trim.Y + 10, spineWidth, trim.Height - 20),
                    Text = "{title}",
                    FontSize = spineFontSize,
                    // Black, because a spine with no art behind it is bare paper, and white
                    // text on white is the kind of default nobody notices until it is printed.
                    Color = "#000000",
                    Rotation = 90,
                    Align = "center",
                },
            },
        };
    }

    private static CoverTemplate SinglePanel(
        string id,
        string name,
        CoverMedia media,
        SizeMm page,
        SizeMm trimOrigin,
        SizeMm trimSize,
        string slotId,
        string slotLabel)
    {
        var trim = new RectMm(trimOrigin.Width, trimOrigin.Height, trimSize.Width, trimSize.Height);

        return new CoverTemplate
        {
            Id = id,
            Name = name,
            Family = "Steam",
            Media = media,
            Page = page,
            Trim = trim,
            Bleed = 2,
            DrawCropMarks = false,
            SourceDpi = SgcDpi,
            Author = SgcAttribution,
            Source = SgcAttribution,
            Terms = SgcTerms,
            Slots =
            {
                new CoverSlot
                {
                    Id = slotId,
                    Label = slotLabel,
                    Bounds = trim,
                    PreferredAspect = trim.AspectRatio,
                },
            },
        };
    }

    private static CoverTemplate BlankWrap(
        string id,
        string name,
        CoverMedia media,
        SizeMm trimSize,
        double spineWidth,
        double spineFontSize)
    {
        var template = Wrap(
            id,
            name,
            media,
            new SizeMm(trimSize.Width + (DefaultBleed * 2), trimSize.Height + (DefaultBleed * 2)),
            new SizeMm(DefaultBleed, DefaultBleed),
            trimSize,
            spineWidth,
            spineFontSize);

        template.Family = "Blank";
        template.Bleed = DefaultBleed;
        template.DrawCropMarks = true;
        template.Author = null;
        template.Source = null;
        template.Terms = null;
        template.SourceDpi = null;
        return template;
    }

    private static CoverTemplate BlankPanel(
        string id,
        string name,
        CoverMedia media,
        SizeMm trimSize,
        string slotId)
    {
        var template = SinglePanel(
            id,
            name,
            media,
            new SizeMm(trimSize.Width + (DefaultBleed * 2), trimSize.Height + (DefaultBleed * 2)),
            new SizeMm(DefaultBleed, DefaultBleed),
            trimSize,
            slotId,
            slotId);

        template.Family = "Blank";
        template.Bleed = DefaultBleed;
        template.DrawCropMarks = true;
        template.Author = null;
        template.Source = null;
        template.Terms = null;
        template.SourceDpi = null;
        return template;
    }
}
