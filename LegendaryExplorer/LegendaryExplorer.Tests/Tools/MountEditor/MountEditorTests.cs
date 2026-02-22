using LegendaryExplorer.Tools.MountEditor;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Tools.MountEditor;

[TestClass]
public class MountEditorTests
{
    // Helpers for building valid inputs per game so individual tests can vary one parameter at a time
    private static string ValidPriority => "100";
    private static string ValidTLKID => "185330";
    private static string ValidDLCFolder => "DLC_MOD_TestMod";
    private static string ValidHumanName => "Test Mod Name";

    private static int NoFlags(MEGame game) => 0;

    private static int SaveFileDependencyFlag(MEGame game) =>
        game.IsGame2() ? (int)EME2MountFileFlag.SaveFileDependency
                       : (int)EME3MountFileFlag.SaveFileDependency;

    // ------------------------------------------------------------------
    // SaveFileDependency flag
    // ------------------------------------------------------------------

    [TestMethod]
    [DataRow(MEGame.ME2)]
    [DataRow(MEGame.LE2)]
    public void ValidateMountFile_RejectsSaveFileDependencyFlag_Game2(MEGame game)
    {
        var result = MountEditorWindow.ValidateMountFile(
            ValidPriority, ValidTLKID, ValidDLCFolder, ValidHumanName,
            SaveFileDependencyFlag(game), game);

        Assert.IsNotNull(result, "SaveFileDependency flag should be rejected");
    }

    [TestMethod]
    [DataRow(MEGame.ME3)]
    [DataRow(MEGame.LE3)]
    public void ValidateMountFile_RejectsSaveFileDependencyFlag_Game3(MEGame game)
    {
        var result = MountEditorWindow.ValidateMountFile(
            ValidPriority, ValidTLKID, ValidDLCFolder, ValidHumanName,
            SaveFileDependencyFlag(game), game);

        Assert.IsNotNull(result, "SaveFileDependency flag should be rejected");
    }

    // ------------------------------------------------------------------
    // Mount priority
    // ------------------------------------------------------------------

    [TestMethod]
    public void ValidateMountFile_RejectsNonNumericMountPriority()
    {
        var result = MountEditorWindow.ValidateMountFile(
            "abc", ValidTLKID, ValidDLCFolder, ValidHumanName,
            NoFlags(MEGame.ME3), MEGame.ME3);

        Assert.IsNotNull(result);
    }

    [TestMethod]
    public void ValidateMountFile_RejectsMountPriorityAboveUshortMax()
    {
        var result = MountEditorWindow.ValidateMountFile(
            "65536", ValidTLKID, ValidDLCFolder, ValidHumanName,
            NoFlags(MEGame.ME3), MEGame.ME3);

        Assert.IsNotNull(result);
    }

    [TestMethod]
    public void ValidateMountFile_AcceptsMountPriorityAtUshortMax()
    {
        var result = MountEditorWindow.ValidateMountFile(
            "65535", ValidTLKID, ValidDLCFolder, ValidHumanName,
            NoFlags(MEGame.ME3), MEGame.ME3);

        Assert.IsNull(result);
    }

    // ------------------------------------------------------------------
    // TLK ID
    // ------------------------------------------------------------------

    [TestMethod]
    public void ValidateMountFile_RejectsNonNumericTLKID()
    {
        var result = MountEditorWindow.ValidateMountFile(
            ValidPriority, "xyz", ValidDLCFolder, ValidHumanName,
            NoFlags(MEGame.ME3), MEGame.ME3);

        Assert.IsNotNull(result);
    }

    [TestMethod]
    public void ValidateMountFile_RejectsZeroTLKID()
    {
        var result = MountEditorWindow.ValidateMountFile(
            ValidPriority, "0", ValidDLCFolder, ValidHumanName,
            NoFlags(MEGame.ME3), MEGame.ME3);

        Assert.IsNotNull(result);
    }

    [TestMethod]
    public void ValidateMountFile_RejectsNegativeTLKID()
    {
        var result = MountEditorWindow.ValidateMountFile(
            ValidPriority, "-1", ValidDLCFolder, ValidHumanName,
            NoFlags(MEGame.ME3), MEGame.ME3);

        Assert.IsNotNull(result);
    }

    [TestMethod]
    public void ValidateMountFile_AcceptsMinimumValidTLKID()
    {
        var result = MountEditorWindow.ValidateMountFile(
            ValidPriority, "1", ValidDLCFolder, ValidHumanName,
            NoFlags(MEGame.ME3), MEGame.ME3);

        Assert.IsNull(result);
    }

    // ------------------------------------------------------------------
    // ME2-specific: human-readable name length
    // ------------------------------------------------------------------

    [TestMethod]
    public void ValidateMountFile_RejectsShortHumanReadableName_ME2()
    {
        var result = MountEditorWindow.ValidateMountFile(
            ValidPriority, ValidTLKID, ValidDLCFolder, "Hi",
            NoFlags(MEGame.ME2), MEGame.ME2);

        Assert.IsNotNull(result, "Human readable name shorter than 5 chars should be rejected for ME2");
    }

    [TestMethod]
    public void ValidateMountFile_AcceptsExactlyFiveCharHumanReadableName_ME2()
    {
        var result = MountEditorWindow.ValidateMountFile(
            ValidPriority, ValidTLKID, ValidDLCFolder, "Hello",
            NoFlags(MEGame.ME2), MEGame.ME2);

        Assert.IsNull(result, "Human readable name of exactly 5 chars should be accepted for ME2");
    }

    [TestMethod]
    public void ValidateMountFile_DoesNotCheckHumanReadableName_LE2()
    {
        // The check is only for the OT ME2; LE2 skips the human name length requirement
        var result = MountEditorWindow.ValidateMountFile(
            ValidPriority, ValidTLKID, ValidDLCFolder, "Hi",
            NoFlags(MEGame.LE2), MEGame.LE2);

        Assert.IsNull(result, "Human readable name length should not be checked for LE2");
    }

    // ------------------------------------------------------------------
    // ME2/LE2: DLC folder name prefix
    // ------------------------------------------------------------------

    [TestMethod]
    [DataRow(MEGame.ME2)]
    [DataRow(MEGame.LE2)]
    public void ValidateMountFile_RejectsDLCFolderWithoutPrefix(MEGame game)
    {
        var result = MountEditorWindow.ValidateMountFile(
            ValidPriority, ValidTLKID, "MyMod", ValidHumanName,
            NoFlags(game), game);

        Assert.IsNotNull(result, "DLC folder not starting with 'DLC_' should be rejected");
    }

    [TestMethod]
    [DataRow(MEGame.ME2)]
    [DataRow(MEGame.LE2)]
    public void ValidateMountFile_AcceptsDLCFolderWithPrefix(MEGame game)
    {
        var result = MountEditorWindow.ValidateMountFile(
            ValidPriority, ValidTLKID, "DLC_MOD_MyMod", ValidHumanName,
            NoFlags(game), game);

        Assert.IsNull(result);
    }

    // ------------------------------------------------------------------
    // Full valid inputs per game
    // ------------------------------------------------------------------

    [TestMethod]
    [DataRow(MEGame.ME2)]
    [DataRow(MEGame.ME3)]
    [DataRow(MEGame.LE2)]
    [DataRow(MEGame.LE3)]
    public void ValidateMountFile_ReturnsNullForValidInputs(MEGame game)
    {
        var result = MountEditorWindow.ValidateMountFile(
            ValidPriority, ValidTLKID, ValidDLCFolder, ValidHumanName,
            NoFlags(game), game);

        Assert.IsNull(result, $"Expected validation to pass for {game}");
    }
}
