using AutoFantic.Core.Hardware;

namespace AutoFantic.Core.Tests;

public class BoardNamesTests
{
    private const string RalfsBoard = "ASRock X870 Steel Legend WiFi";

    [Theory]
    [InlineData("/lpc/nct6686d/0/control/0", "CPU Fan", "CPU Fan 1")]
    [InlineData("/lpc/nct6686d/0/control/1", "Pump Fan", "CPU Fan 2")] // the library's list for this chip is another maker's
    [InlineData("/lpc/nct6686d/0/fan/1", "Pump Fan", "CPU Fan 2")] // its RPM sensor goes by the same name
    public void A_known_board_gets_the_names_printed_on_it(string id, string fromLibrary, string onBoard)
    {
        Assert.Equal(onBoard, BoardNames.For(RalfsBoard, id, fromLibrary));
        Assert.Equal(onBoard, BoardNames.For(RalfsBoard.ToUpperInvariant(), id, fromLibrary));
    }

    [Theory]
    [InlineData(RalfsBoard, "/lpc/nct6686d/0/control/5", "System Fan #4")] // an output nobody checked on this board
    [InlineData(RalfsBoard, "/lpc/nct6686d/0/temperature/1", "System")] // not a fan
    [InlineData(RalfsBoard, "/lpc/nct6799d/0/control/1", "Pump Fan")] // another fan chip
    [InlineData(RalfsBoard, "/gpu-nvidia/0/control/1", "GPU Fan 1")]
    [InlineData("Micro-Star International Co., Ltd. MAG B650 TOMAHAWK WIFI (MS-7D75)", "/lpc/nct6686d/0/control/1", "Pump Fan")]
    [InlineData(null, "/lpc/nct6686d/0/control/1", "Pump Fan")]
    public void Everything_else_keeps_the_librarys_name(string? board, string id, string name)
    {
        Assert.Equal(name, BoardNames.For(board, id, name));
    }

    [Theory]
    [InlineData("ASRock", "X870 Steel Legend WiFi", RalfsBoard)]
    [InlineData(" ASRock ", " X870 Steel Legend WiFi ", RalfsBoard)]
    [InlineData("ASRock", "ASRock X870 Steel Legend WiFi", RalfsBoard)] // the maker isn't said twice
    [InlineData("", "X870 Steel Legend WiFi", "X870 Steel Legend WiFi")]
    [InlineData("ASRock", null, "ASRock")]
    [InlineData(null, "", null)]
    public void Make_and_model_become_one_name(string? maker, string? model, string? board)
    {
        Assert.Equal(board, BoardNames.Describe(maker, model));
    }
}
