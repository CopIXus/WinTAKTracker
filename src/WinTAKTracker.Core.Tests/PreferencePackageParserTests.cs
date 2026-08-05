using System.IO.Compression;
using System.Text;
using WinTAKTracker.Services.Identity;
using WinTAKTracker.Services.Tak;
using Xunit;

namespace WinTAKTracker.Core.Tests;

public class PreferencePackageParserTests
{
    private const string ConfigPref = """
        <?xml version='1.0' encoding='ASCII' standalone='yes'?>
        <preferences>
          <preference version="1" name="com.atakmap.app_civ_preferences">
            <entry key="locationCallsign" class="class java.lang.String">ANDROID-NAMEHERE</entry>
            <entry key="locationTeam" class="class java.lang.String">Dark Green</entry>
            <entry key="atakRoleType" class="class java.lang.String">Team Member</entry>
          </preference>
          <preference version="1" name="com.atakmap.app_preferences">
            <entry key="locationCallsign" class="class java.lang.String">ANDROID-NAMEHERE</entry>
            <entry key="locationTeam" class="class java.lang.String">Dark Green</entry>
            <entry key="atakRoleType" class="class java.lang.String">Team Member</entry>
          </preference>
        </preferences>
        """;

    private static byte[] PrefZip(string onReceiveImport = "true")
    {
        var manifest = $$"""
            <MissionPackageManifest version="2">
              <Configuration>
                <Parameter name="uid" value="11111111-2222-3333-4444-555555555555"/>
                <Parameter name="name" value="Pref-ANDROID-NAMEHERE-Dark-Green-Team-Member.zip"/>
                <Parameter name="onReceiveImport" value="{{onReceiveImport}}"/>
                <Parameter name="onReceiveDelete" value="false"/>
              </Configuration>
              <Contents>
                <Content ignore="false" zipEntry="certs/config.pref">
                  <Parameter name="name" value="Preference Configuration"/>
                </Content>
              </Contents>
            </MissionPackageManifest>
            """;

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var man = zip.CreateEntry("MANIFEST/manifest.xml");
            using (var w = new StreamWriter(man.Open(), Encoding.UTF8))
                w.Write(manifest);
            var pref = zip.CreateEntry("certs/config.pref");
            using (var w = new StreamWriter(pref.Open(), Encoding.UTF8))
                w.Write(ConfigPref);
        }

        return ms.ToArray();
    }

    [Fact]
    public void ParsePrefXml_ReadsAtakRoleTypeAndDarkGreen()
    {
        var prefs = PreferencePackageParser.ParsePrefXml(ConfigPref);
        Assert.Equal("ANDROID-NAMEHERE", prefs.Callsign);
        Assert.Equal("Dark Green", prefs.Team);
        Assert.Equal("Team Member", prefs.Role);
    }

    [Fact]
    public void ParseZip_PrefPackage_AutoImportTrue()
    {
        var bytes = PrefZip("true");
        Assert.True(PreferencePackageParser.IsPreferencePackage(
            bytes, "Pref-ANDROID-NAMEHERE-Dark-Green-Team-Member.zip"));
        var prefs = PreferencePackageParser.ParseZipBytes(bytes);
        Assert.True(prefs.HasAny);
        Assert.True(prefs.OnReceiveImport);
        Assert.True(PreferencePackageParser.ShouldAutoImport(prefs));
        Assert.Equal("ANDROID-NAMEHERE", prefs.Callsign);
        Assert.Equal("Dark Green", prefs.Team);
        Assert.Equal("Team Member", prefs.Role);
    }

    [Fact]
    public void ParseZip_OnReceiveImportFalse_SkipsAutoImport()
    {
        var prefs = PreferencePackageParser.ParseZipBytes(PrefZip("false"));
        Assert.False(prefs.OnReceiveImport);
        Assert.False(PreferencePackageParser.ShouldAutoImport(prefs));
    }

    [Fact]
    public void Apply_AppendsWttSuffix()
    {
        var cfg = new WinTAKTracker.Services.Config.AppConfig();
        var result = RemoteIdentityApply.Apply(cfg, "ANDROID-NAMEHERE", "Dark Green", "Team Member");
        Assert.True(result.Applied);
        Assert.Equal("ANDROID-NAMEHERE.wtt", cfg.ComputerIdentity.Callsign);
        Assert.Equal("Dark Green", cfg.ComputerIdentity.Team);
        Assert.Equal("Team Member", cfg.ComputerIdentity.Role);
    }

    [Fact]
    public void FileShareCot_ParsesPrefAnnounce()
    {
        var xml = """
            <event version="2.0" uid="fs-1" type="b-f-t-r" how="h-e" time="2026-01-01T00:00:00.000Z" start="2026-01-01T00:00:00.000Z" stale="2026-01-01T00:01:00.000Z">
              <point lat="0" lon="0" hae="0" ce="9999999" le="9999999"/>
              <detail>
                <fileshare filename="Pref-ANDROID-NAMEHERE-Dark-Green-Team-Member.zip"
                           senderUrl="https://tak.example.com:8443/Marti/sync/content?hash=abc123"
                           sha256="abc123" sizeInBytes="2048"/>
              </detail>
            </event>
            """;
        Assert.True(FileShareCotParser.LooksLikeFileShareEvent(xml));
        var offer = FileShareCotParser.TryParse(xml)!;
        Assert.True(offer.LooksLikePreferencePackage);
        Assert.Equal("abc123", offer.Sha256);
        Assert.Contains("Marti/sync/content", offer.SenderUrl);
    }
}
