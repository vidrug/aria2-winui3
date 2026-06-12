using System.Text;
using Aria2Gui.Services;
using Xunit;

namespace Aria2Gui.Tests;

public class TorrentParserTests
{
    private static byte[] B(string bencode) => Encoding.UTF8.GetBytes(bencode);

    [Fact]
    public void Parses_single_file_torrent()
    {
        var t = TorrentParser.Parse(B("d4:infod4:name8:file.iso6:lengthi1024eee"));

        Assert.True(t.IsSingleFile);
        Assert.Equal("file.iso", t.Name);
        var file = Assert.Single(t.Files);
        Assert.Equal(1, file.Index);
        Assert.Equal(1024, file.Length);
        Assert.Equal("file.iso", file.PathSegments[^1]);
    }

    [Fact]
    public void Parses_multi_file_torrent_with_folders()
    {
        var t = TorrentParser.Parse(B(
            "d4:infod4:name3:dir5:filesl" +
            "d6:lengthi10e4:pathl3:sub5:a.txtee" +
            "d6:lengthi20e4:pathl5:b.txtee" +
            "eee"));

        Assert.False(t.IsSingleFile);
        Assert.Equal(2, t.Files.Count);
        Assert.Equal(["sub", "a.txt"], t.Files[0].PathSegments);
        Assert.Equal(10, t.Files[0].Length);
        Assert.Equal(1, t.Files[0].Index);
        Assert.Equal(2, t.Files[1].Index); // 1-based, what aria2 select-file expects
    }

    [Fact]
    public void Falls_back_to_legacy_path_when_utf8_path_is_empty()
    {
        // path.utf-8 present but holding only empty segments -> legacy "path" wins (B19).
        var t = TorrentParser.Parse(B(
            "d4:infod4:name3:dir5:filesl" +
            "d6:lengthi10e4:pathl5:realAe10:path.utf-8l0:ee" +
            "eee"));

        Assert.Equal("realA", t.Files[0].PathSegments[^1]);
    }

    [Fact]
    public void Nesting_bomb_throws_instead_of_overflowing_the_stack()
    {
        // 5000 nested lists would overflow the stack without the depth cap (B1).
        string bomb = new string('l', 5000) + new string('e', 5000);
        Assert.Throws<InvalidDataException>(() => TorrentParser.Parse(B(bomb)));
    }

    [Fact]
    public void Huge_declared_string_length_throws_cleanly()
    {
        // Declared length near int.MaxValue wrapped the int bounds check and hit a ~2 GB
        // allocation before the long-arithmetic fix (N19).
        Assert.Throws<InvalidDataException>(() => TorrentParser.Parse(B("d2147483640:x")));
    }

    [Theory]
    [InlineData("d4:infodi99e4:name3:diree")] // non-string dict key
    [InlineData("d4:info")]                   // truncated
    [InlineData("dXe")]                       // unexpected byte
    [InlineData("d1:ai12345")]                // unclosed int
    [InlineData("d1:aiNOTANUMBERee")]         // garbage int (B18: TryParse, not Parse)
    [InlineData("i42e")]                      // root is not a dict
    [InlineData("d1:a1:be")]                  // no "info" dict
    public void Malformed_input_throws_InvalidDataException(string bencode) =>
        Assert.Throws<InvalidDataException>(() => TorrentParser.Parse(B(bencode)));

    [Fact]
    public void Missing_path_gets_a_placeholder_name()
    {
        var t = TorrentParser.Parse(B("d4:infod4:name3:dir5:filesld6:lengthi10eeeee"));
        Assert.Equal("file1", t.Files[0].PathSegments[^1]);
    }
}
