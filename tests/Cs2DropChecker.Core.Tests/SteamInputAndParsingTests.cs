using Cs2DropChecker.Core.Models;
using Cs2DropChecker.Core.Pricing;
using Cs2DropChecker.Core.Steam;

namespace Cs2DropChecker.Core.Tests;

public class SteamInputAndParsingTests
{
    [Theory]
    [InlineData("76561198000000042", 76561198000000042UL, null)]
    [InlineData("https://steamcommunity.com/profiles/76561198000000042/", 76561198000000042UL, null)]
    [InlineData("steamcommunity.com/profiles/76561198000000042/inventory#730", 76561198000000042UL, null)]
    [InlineData("https://steamcommunity.com/id/ohnepixel", 0UL, "ohnepixel")]
    [InlineData("https://steamcommunity.com/id/kova_cs/?xml=1", 0UL, "kova_cs")]
    [InlineData("  katana_cs ", 0UL, "katana_cs")]
    public void Parses_profile_inputs(string input, ulong id, string? vanity)
    {
        Assert.True(SteamIdInput.TryParse(input, out var parsedId, out var parsedVanity));
        Assert.Equal(id, parsedId);
        Assert.Equal(vanity, parsedVanity);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello world")]
    [InlineData("12345678901234567")]
    [InlineData("https://example.com/foo")]
    public void Rejects_bad_inputs(string input) => Assert.False(SteamIdInput.TryParse(input, out _, out _));

    [Theory]
    [InlineData("28,86€", "28.86")]
    [InlineData("$1,234.56", "1234.56")]
    [InlineData("1.234,56€", "1234.56")]
    [InlineData("12,--€", "12.00")]
    [InlineData("1 234,56 zł", "1234.56")]
    [InlineData("R$ 0,03", "0.03")]
    [InlineData("CDN$ 5.00", "5.00")]
    [InlineData("£0.5", "0.5")]
    [InlineData("1,234", "1234")]
    public void Parses_steam_prices(string text, string expected) =>
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), SteamPriceParser.Parse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("--")]
    [InlineData("€")]
    public void Unparseable_prices_are_null(string? text)
    {
        var v = SteamPriceParser.Parse(text);
        Assert.True(v is null || v == 0m);
    }

    [Fact]
    public void Parses_profile_xml()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <profile><steamID64>76561198000000042</steamID64><steamID><![CDATA[test player]]></steamID>
            <privacyState>public</privacyState><avatarMedium><![CDATA[https://avatars.example/a_medium.jpg]]></avatarMedium></profile>
            """;
        var p = SteamProfileClient.ParseProfileXml(xml, 0);
        Assert.Equal(76561198000000042UL, p.SteamId);
        Assert.Equal("test player", p.Name);
        Assert.Equal("public", p.Privacy);
        Assert.Equal("https://avatars.example/a_medium.jpg", p.AvatarUrl);
    }

    [Fact]
    public void Profile_xml_error_throws()
    {
        const string xml = "<?xml version=\"1.0\"?><response><error><![CDATA[The specified profile could not be found.]]></error></response>";
        Assert.Throws<SteamLookupException>(() => SteamProfileClient.ParseProfileXml(xml, 0));
    }

    [Fact]
    public void Formats_currencies()
    {
        Assert.Equal("€2,184.37", Currencies.Format(2184.37m, "EUR"));
        Assert.Equal("12.50 zł", Currencies.Format(12.5m, "PLN"));
        Assert.Equal("-$1.00", Currencies.Format(-1m, "USD"));
    }

    private const string InventoryJson = """
        {"assets":[
          {"appid":730,"contextid":"2","assetid":"100","classid":"11","instanceid":"0","amount":"1"},
          {"appid":730,"contextid":"2","assetid":"101","classid":"12","instanceid":"5","amount":"1"},
          {"appid":730,"contextid":"2","assetid":"102","classid":"13","instanceid":"0","amount":"1"}],
         "descriptions":[
          {"appid":730,"classid":"11","instanceid":"0","icon_url":"abc","tradable":1,"marketable":1,
           "name":"StatTrak™ M4A4 | Temukau","type":"StatTrak™ Covert Rifle","market_hash_name":"StatTrak™ M4A4 | Temukau (Field-Tested)",
           "descriptions":[{"type":"html","value":"Exterior: Field-Tested","name":"exterior_wear"},
             {"type":"html","value":"<br><div id=\"sticker_info\"><center><img src=\"a.png\"><img src=\"b.png\"><br>Sticker: A, B</center></div>","name":"sticker_info"}],
           "tags":[{"category":"Type","internal_name":"CSGO_Type_Rifle","localized_tag_name":"Rifle"},
             {"category":"Weapon","internal_name":"weapon_m4a1","localized_tag_name":"M4A4"},
             {"category":"Quality","internal_name":"strange","localized_tag_name":"StatTrak™","color":"CF6A32"},
             {"category":"Rarity","internal_name":"Rarity_Ancient_Weapon","localized_tag_name":"Covert","color":"EB4B4B"},
             {"category":"Exterior","internal_name":"WearCategory2","localized_tag_name":"Field-Tested"}]},
          {"appid":730,"classid":"12","instanceid":"5","icon_url":"def","tradable":0,"marketable":0,
           "name":"Storage Unit","type":"Base Grade Tool","market_hash_name":"Storage Unit",
           "descriptions":[{"type":"html","value":"Number of Items: 37","name":"attr: items count"}],
           "tags":[{"category":"Type","internal_name":"CSGO_Type_Tool","localized_tag_name":"Tool"}]},
          {"appid":730,"classid":"13","instanceid":"0","icon_url":"ghi","tradable":1,"marketable":1,
           "name":"Kilowatt Case","type":"Base Grade Container","market_hash_name":"Kilowatt Case",
           "tags":[{"category":"Type","internal_name":"CSGO_Type_WeaponCase","localized_tag_name":"Container"},
             {"category":"Rarity","internal_name":"Rarity_Common","localized_tag_name":"Base Grade","color":"b0c3d9"}]}],
         "asset_properties":[{"appid":730,"contextid":"2","assetid":"100","asset_properties":[
            {"propertyid":1,"int_value":"89","name":"Pattern Template"},
            {"propertyid":2,"float_value":"0.169311","name":"Wear Rating"},
            {"propertyid":5,"string_value":"Lucky","name":"Name Tag"}]}],
         "more_items":1,"last_assetid":"102","total_inventory_count":5,"success":1,"rwgrsn":-2}
        """;

    [Fact]
    public void Parses_inventory_page()
    {
        var page = InventoryParser.Parse(InventoryJson);
        Assert.True(page.Success);
        Assert.True(page.MoreItems);
        Assert.Equal("102", page.LastAssetId);
        Assert.Equal(5, page.TotalCount);
        Assert.Equal(3, page.Items.Count);

        var m4 = page.Items[0];
        Assert.Equal("StatTrak™ M4A4", m4.WeaponTitle);
        Assert.Equal("Temukau", m4.SkinTitle);
        Assert.True(m4.IsStatTrak);
        Assert.Equal("eb4b4b", m4.RarityHex);
        Assert.Equal(0.169311, m4.Float);
        Assert.Equal(89, m4.PaintSeed);
        Assert.Equal("Lucky", m4.CustomName);
        Assert.Equal(2, m4.StickerCount);
        Assert.Equal("FT · 0.1693", m4.WearLine);
        Assert.Contains("2 stickers", m4.ValueFlags);

        var unit = page.Items[1];
        Assert.True(unit.IsStorageUnit);
        Assert.Equal(37, unit.StorageCount);

        var box = page.Items[2];
        Assert.True(box.IsContainer);
        Assert.Equal("Container", box.WeaponTitle);
        Assert.Equal("Kilowatt Case", box.SkinTitle);
        Assert.Equal("Base Grade Container", box.RarityLine);
        Assert.Equal("No wear", box.WearLine);
    }

    [Fact]
    public void Empty_and_null_bodies_are_not_success()
    {
        Assert.False(InventoryParser.Parse("null").Success);
        Assert.False(InventoryParser.Parse("").Success);
        var empty = InventoryParser.Parse("""{"total_inventory_count":0,"success":1,"rwgrsn":-2}""");
        Assert.True(empty.Success);
        Assert.Empty(empty.Items);
    }

    private sealed class FakeTransport(Dictionary<string, TransportResponse> pages, bool signedIn = false) : IInventoryTransport
    {
        public List<string> Requests { get; } = new();
        public bool IsSignedIn => signedIn;

        public Task<TransportResponse> GetAsync(string pathAndQuery, CancellationToken ct)
        {
            Requests.Add(pathAndQuery);
            foreach (var (key, value) in pages)
                if (pathAndQuery.Contains(key, StringComparison.Ordinal)) return Task.FromResult(value);
            return Task.FromResult(new TransportResponse(500, ""));
        }
    }

    [Fact]
    public async Task Fetcher_pages_and_tolerates_missing_protected_context()
    {
        var page2 = """{"assets":[{"appid":730,"contextid":"2","assetid":"103","classid":"13","instanceid":"0","amount":"1"}],"descriptions":[{"classid":"13","instanceid":"0","name":"Kilowatt Case","market_hash_name":"Kilowatt Case","tags":[]}],"total_inventory_count":5,"success":1}""";
        var t = new FakeTransport(new()
        {
            ["/730/2?l=english&count=2000&start_assetid=102"] = new(200, page2),
            ["/730/2?l=english&count=2000"] = new(200, InventoryJson),
            ["/730/16"] = new(403, "null"),
        });
        var fetcher = new InventoryFetcher { PageDelay = TimeSpan.Zero };
        var r = await fetcher.FetchAsync(t, 76561198000000042UL, ["2", "16"], CancellationToken.None);
        Assert.Equal(FetchStatus.Ok, r.Status);
        Assert.Equal(4, r.Snapshot!.Items.Count);
        Assert.Equal(5, r.Snapshot.TotalCount);
        Assert.Equal(1, r.Snapshot.HiddenCount);
        Assert.Equal(3, t.Requests.Count);
    }

    [Theory]
    [InlineData(403, FetchStatus.Private)]
    [InlineData(401, FetchStatus.Private)]
    [InlineData(404, FetchStatus.NotFound)]
    [InlineData(429, FetchStatus.RateLimited)]
    [InlineData(502, FetchStatus.Failed)]
    public async Task Fetcher_maps_errors(int status, FetchStatus expected)
    {
        var t = new FakeTransport(new() { ["/730/2"] = new(status, "null") });
        var r = await new InventoryFetcher { PageDelay = TimeSpan.Zero }.FetchAsync(t, 76561198000000042UL, ["2"], CancellationToken.None);
        Assert.Equal(expected, r.Status);
        Assert.Null(r.Snapshot);
    }

    [Fact]
    public async Task Signed_in_401_means_session_expired()
    {
        var t = new FakeTransport(new() { ["/730/2"] = new(401, "null") }, signedIn: true);
        var r = await new InventoryFetcher { PageDelay = TimeSpan.Zero }.FetchAsync(t, 76561198000000042UL, ["2"], CancellationToken.None);
        Assert.Equal(FetchStatus.SignedOut, r.Status);
    }
}
