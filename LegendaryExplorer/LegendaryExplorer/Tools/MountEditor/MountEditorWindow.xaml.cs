using System;
using Microsoft.WindowsAPICodePack.Dialogs;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorer.Tools.TlkManagerNS;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.TLK;
using LegendaryExplorerCore.Unreal;
using Xceed.Wpf.Toolkit.Primitives;

namespace LegendaryExplorer.Tools.MountEditor
{
    /// <summary>
    /// Interaction logic for MountEditorWPF.xaml
    /// </summary>
    public partial class MountEditorWindow : TrackingNotifyPropertyChangedWindowBase
    {
        public ObservableCollectionExtended<MountFlag> MountOptions { get; } = new();

        public ObservableCollectionExtended<UIGameID> Games { get; } = new()
        {
            new UIGameID(MEGame.ME2, "Mass Effect 2"),
            new UIGameID(MEGame.ME3, "Mass Effect 3"),
            new UIGameID(MEGame.LE2, "Mass Effect 2 LE"),
            new UIGameID(MEGame.LE3, "Mass Effect 3 LE")
        };

        private UIGameID _selectedGame;
        public UIGameID SelectedGame
        {
            get => _selectedGame;
            set
            {
                if (SetProperty(ref _selectedGame, value))
                {
                    SelectedGameChanged();
                }
            }
        }

        private bool _isME2;
        public bool IsME2
        {
            get => _isME2;
            set => SetProperty(ref _isME2, value);
        }

        private string _currentTLKIDString;
        public string CurrentTLKIDString
        {
            get => _currentTLKIDString;
            set => SetProperty(ref _currentTLKIDString, value);
        }

        private string _currentMountFileText;
        public string CurrentMountFileText
        {
            get => _currentMountFileText;
            set => SetProperty(ref _currentMountFileText, value);
        }

        private string _mountPriorityText = "";
        public string MountPriorityText
        {
            get => _mountPriorityText;
            set => SetProperty(ref _mountPriorityText, value);
        }

        private string _tlkIDText = "";
        public string TLKIDText
        {
            get => _tlkIDText;
            set
            {
                if (SetProperty(ref _tlkIDText, value) && int.TryParse(value, out int tlkValue))
                {
                    CurrentTLKIDString = TLKManagerWPF.GlobalFindStrRefbyID(tlkValue, SelectedGame.Game);
                }
            }
        }

        private string _dlcFolderName = "";
        public string DLCFolderName
        {
            get => _dlcFolderName;
            set => SetProperty(ref _dlcFolderName, value);
        }

        private string _humanReadableName = "";
        public string HumanReadableName
        {
            get => _humanReadableName;
            set => SetProperty(ref _humanReadableName, value);
        }

        private string _dlcFolderWatermark = "";
        public string DLCFolderWatermark
        {
            get => _dlcFolderWatermark;
            set => SetProperty(ref _dlcFolderWatermark, value);
        }

        private string _humanReadableWatermark = "";
        public string HumanReadableWatermark
        {
            get => _humanReadableWatermark;
            set => SetProperty(ref _humanReadableWatermark, value);
        }

        public MountEditorWindow() : base("Mount Editor", true)
        {
            CurrentMountFileText = "No mount file loaded. Mouse over fields for descriptions of their values.";
            DataContext = this;
            InitializeComponent();
            SelectedGame = Games[0];
        }

        private void PreviewIntegerInput(object sender, TextCompositionEventArgs e)
        {
            var textBox = sender as TextBox;
            var fullText = textBox.Text.Insert(textBox.SelectionStart, e.Text);
            e.Handled = !double.TryParse(fullText, out double _);
        }

        public sealed record UIGameID(MEGame Game, string DisplayString);

        private void LoadMountFile_Click(object sender, RoutedEventArgs e)
        {
            CommonOpenFileDialog m = new()
            {
                EnsurePathExists = true,
                Title = "Select Mount.dlc file",
            };

            m.Filters.Add(new CommonFileDialogFilter("Mount files", "*.dlc"));
            if (m.ShowDialog(this) == CommonFileDialogResult.Ok)
            {
                LoadFile(m.FileName);
            }
        }

        public void LoadFile(string fileName)
        {
            loadingNewData = true;
            var mf = new MountFile(fileName);
            SelectedGame = Games.First(uig => uig.Game == mf.Game);
            DLCFolderName = IsME2 ? mf.ME2Only_DLCFolderName : "Not used in ME3";
            HumanReadableName = IsME2 ? mf.ME2Only_DLCHumanName : "Not used in ME3";
            TLKIDText = mf.TLKID.ToString();
            MountPriorityText = mf.MountPriority.ToString();

            // Mount flags
            if (IsME2)
            {
                var flagset = Enum.GetValues<EME2MountFileFlag>();
                MountOptions.ReplaceAll(flagset.Select(x => new MountFlag((int)x, true)));
            }
            else
            {
                var flagset = Enum.GetValues<EME3MountFileFlag>();
                MountOptions.ReplaceAll(flagset.Select(x => new MountFlag((int)x, false)));
            }

            CurrentMountFileText = fileName;
            SetSelectedFlagsUI(mf.MountFlags.FlagValue);
            loadingNewData = false;
        }

        private void SetSelectedFlagsUI(int flag)
        {
            if (IsME2)
            {
                var flagset = Enum.GetValues<EME2MountFileFlag>();
                var selectedFlags = flagset.Where(testflag => ((int)testflag & flag) != 0).ToList();
                foreach (var item in MountOptions)
                {
                    item.IsUISelected = selectedFlags.Any(x => x == (EME2MountFileFlag)item.FlagValue);
                }
            }
            else
            {
                var flagset = Enum.GetValues<EME3MountFileFlag>();
                var selectedFlags = flagset.Where(testflag => ((int)testflag & flag) != 0).ToList();
                foreach (var item in MountOptions)
                {
                    item.IsUISelected = selectedFlags.Any(x => x == (EME3MountFileFlag)item.FlagValue);
                }
            }
        }

        private void SaveMountFile_Click(object sender, RoutedEventArgs e)
        {
            if (Validate())
            {
                CommonSaveFileDialog m = new()
                {
                    EnsurePathExists = true,
                    Title = "Select Mount.dlc file save destination",
                    DefaultExtension = "dlc",
                    AlwaysAppendDefaultExtension = true,
                    DefaultFileName = "mount.dlc",
                    InitialDirectory = (!string.IsNullOrEmpty(CurrentMountFileText) && File.Exists(CurrentMountFileText) ? Path.GetDirectoryName(CurrentMountFileText) : null)
                };
                m.Filters.Add(new CommonFileDialogFilter("Mount files", "*.dlc"));
                if (m.ShowDialog() == CommonFileDialogResult.Ok)
                {
                    var mf = new MountFile()
                    {
                        Game = SelectedGame.Game,
                        MountPriority = ushort.Parse(MountPriorityText.Trim()),
                        TLKID = int.Parse(TLKIDText.Trim()),
                        MountFlags = GetCurrentMountFlag()
                    };

                    if (mf.Game.IsGame2())
                    {
                        mf.ME2Only_DLCFolderName = DLCFolderName;
                        mf.ME2Only_DLCHumanName = HumanReadableName;
                    }
                    mf.WriteMountFile(m.FileName);
                    MessageBox.Show("Done.");
                }
            }
        }

        private MountFlag GetCurrentMountFlag()
        {
            MountFlag mf = new MountFlag(0, IsME2);
            foreach (var flag in MountOptions.Where(x => x.IsUISelected))
            {
                mf.SetFlagBit(flag.FlagValue);
            }

            return mf;
        }

        private bool Validate()
        {
            var errorMessage = ValidateMountFile(MountPriorityText, TLKIDText, DLCFolderName, HumanReadableName,
                GetCurrentMountFlag().FlagValue, SelectedGame.Game);
            if (errorMessage is not null)
            {
                Xceed.Wpf.Toolkit.MessageBox.Show(errorMessage, "Validation error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Validates mount file parameters. Returns an error message if invalid, or <c>null</c> if valid.
        /// </summary>
        /// <param name="mountPriorityText">Text representation of the mount priority value</param>
        /// <param name="tlkIdText">Text representation of the TLK string reference ID</param>
        /// <param name="dlcFolderName">DLC folder name (ME2/LE2 only)</param>
        /// <param name="humanReadableName">Human-readable DLC name (ME2 only)</param>
        /// <param name="flagValue">Combined mount flag bit-mask value</param>
        /// <param name="game">Target game for this mount file</param>
        /// <returns>An error message string if validation fails; <c>null</c> if all parameters are valid.</returns>
        public static string? ValidateMountFile(string mountPriorityText, string tlkIdText, string dlcFolderName,
            string humanReadableName, int flagValue, MEGame game)
        {
            bool isME2 = game.IsGame2();
            var saveDep = isME2 ? (int)EME2MountFileFlag.SaveFileDependency : (int)EME3MountFileFlag.SaveFileDependency;
            if ((saveDep & flagValue) != 0)
            {
                return "Cannot save a mount file with the SaveFileDependency flag set. This flag causes serious issues with save games when used with mods.";
            }

            if (!ushort.TryParse(mountPriorityText, out ushort _))
            {
                return "Mount priority must be a value between 1 and " + ushort.MaxValue + ".";
            }

            if (!int.TryParse(tlkIdText, out int tlkId) || tlkId <= 0)
            {
                return "TLK ID must be between 1 and " + (uint.MaxValue / 2) + ".";
            }

            if (isME2)
            {
                if (game is MEGame.ME2 && humanReadableName.Length < 5)
                {
                    return "Human readable name must be at least 5 characters.\nUse the full name of your mod to prevent end-user confusion.";
                }

                if (!dlcFolderName.StartsWith("DLC_"))
                {
                    return "DLC Folder Name must start with \"DLC_\".\nMass Effect 2 will not load a DLC that does not start with this prefix.";
                }
            }

            return null;
        }

        private void PreviewShortInput(object sender, TextCompositionEventArgs e)
        {
            var textBox = sender as TextBox;
            var fullText = textBox.Text.Insert(textBox.SelectionStart, e.Text);

            var handled = double.TryParse(fullText, out double _);

            if (handled)
            {
                if (int.TryParse(fullText, out int value))
                {
                    e.Handled = value <= 0 || value > short.MaxValue;
                    return;
                }
            }
            e.Handled = true;
        }

        private void SelectedGameChanged()
        {
            IsME2 = SelectedGame.Game is MEGame.ME2 or MEGame.LE2;
            DLCFolderWatermark = IsME2 ? "DLC Folder Name (e.g. DLC_MOD_MYMOD)" : "Not used in ME3";
            HumanReadableWatermark = IsME2 ? "DLC Human Readable Name (e.g. Superpowers Pack)" : "Not used in ME3";
            // Re-evaluate the TLK ID string for the newly selected game
            if (int.TryParse(TLKIDText, out int tlkValue))
            {
                CurrentTLKIDString = TLKManagerWPF.GlobalFindStrRefbyID(tlkValue, SelectedGame.Game);
            }
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                LoadFile(files[0]);
            }
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                string ext = Path.GetExtension(files[0]).ToLower();
                if (ext != ".dlc")
                {
                    e.Effects = DragDropEffects.None;
                    e.Handled = true;
                }
            }
            else
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
            }
        }

        private void MountOptionsComboBox_GotFocus(object sender, RoutedEventArgs e)
        {
        }

        private bool loadingNewData;
        private void MountOptionsComboBox_ItemSelectionChanged(object sender, Xceed.Wpf.Toolkit.Primitives.ItemSelectionChangedEventArgs e)
        {
        }
    }
}
