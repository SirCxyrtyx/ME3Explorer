﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Be.Windows.Forms;
using ME3Explorer.Packages;
using Xceed.Wpf.Toolkit.Primitives;
using static ME3Explorer.EnumExtensions;
using static ME3Explorer.Unreal.UnrealFlags;

namespace ME3Explorer.MetadataEditor
{
    /// <summary>
    /// Interaction logic for MetadataEditorWPF.xaml
    /// </summary>
    public partial class MetadataEditorWPF : ExportLoaderControl
    {
        //This is a ExportLoaderControl as it can technically function as one. It can also function as an ImportLoader. Given that there is really no other
        //use for loading imports into an editor I am going to essentially just add the required load methods in this loader.

        private const int HEADER_OFFSET_EXP_IDXCLASS = 0;
        private const int HEADER_OFFSET_EXP_IDXSUPERCLASS = 4;
        private const int HEADER_OFFSET_EXP_IDXLINK = 8;
        private const int HEADER_OFFSET_EXP_IDXOBJECTNAME = 12;
        private const int HEADER_OFFSET_EXP_INDEXVALUE = 16;
        private const int HEADER_OFFSET_EXP_IDXARCHETYPE = 20;
        private const int HEADER_OFFSET_EXP_OBJECTFLAGS = 24;

        private const int HEADER_OFFSET_IMP_IDXCLASSNAME = 8;
        private const int HEADER_OFFSET_IMP_IDXLINK = 12;
        private const int HEADER_OFFSET_IMP_IDXOBJECTNAME = 20;
        private const int HEADER_OFFSET_IMP_IDXPACKAGEFILE = 0;

        private IEntry CurrentLoadedEntry;
        private List<string> Classes;
        private HexBox Header_Hexbox;
        private bool loadingNewData = false;


        public MetadataEditorWPF()
        {
            InitializeComponent();
        }

        public override bool CanParse(IExportEntry exportEntry)
        {
            return true;
        }

        private void RefreshClassesList(IMEPackage Pcc)
        {
            IReadOnlyList<ImportEntry> imports = Pcc.Imports;
            Classes = new List<string>();
            for (int i = imports.Count - 1; i >= 0; i--)
            {
                Classes.Add($"{-i + 1}: {imports[i].GetFullPath}");
            }
            Classes.Add("0 : Class");
            int count = 1;
            IReadOnlyList<IExportEntry> Exports = Pcc.Exports;
            foreach (IExportEntry exp in Exports)
            {
                Classes.Add($"{count++}: {exp.GetFullPath}");
            }
            InfoTab_PackageLink_ComboBox.ItemsSource = Classes;
        }

        public override void LoadExport(IExportEntry exportEntry)
        {
            loadingNewData = true;
            Info_Header_UnsavedChanges.Visibility = Visibility.Collapsed;
            try
            {
                Row_Archetype.Height = new GridLength(24);
                Row_ExpClass.Height = new GridLength(24);
                Row_Superclass.Height = new GridLength(24);
                Row_ImpClass.Height = new GridLength(0);
                Row_ExpClass.Height = new GridLength(24);
                Row_Packagefile.Height = new GridLength(0);
                Row_ObjectFlags.Height = new GridLength(24);
                Row_ExportDataSize.Height = new GridLength(24);
                Row_ExportDataOffsetDec.Height = new GridLength(24);
                Row_ExportDataOffsetHex.Height = new GridLength(24);
                InfoTab_Link_TextBlock.Text = "0x08 Link:";
                InfoTab_ObjectName_TextBlock.Text = "0x0C Object name:";

                InfoTab_Objectname_ComboBox.SelectedItem = exportEntry.ObjectName;

                if (exportEntry.idxClass != 0)
                {
                    //IEntry _class = pcc.getEntry(exportEntry.idxClass);
                    InfoTab_Class_ComboBox.ItemsSource = Classes;
                    InfoTab_Class_ComboBox.SelectedIndex = exportEntry.idxClass + exportEntry.FileRef.Imports.Count; //make positive
                }
                else
                {
                    InfoTab_Class_ComboBox.SelectedIndex = exportEntry.FileRef.Imports.Count; //Class, 0
                }
                InfoTab_Superclass_ComboBox.ItemsSource = Classes;
                if (exportEntry.idxClassParent != 0)
                {
                    InfoTab_Superclass_ComboBox.SelectedIndex = exportEntry.idxClassParent + exportEntry.FileRef.Imports.Count; //make positive
                }
                else
                {
                    InfoTab_Superclass_ComboBox.SelectedIndex = exportEntry.FileRef.Imports.Count; //Class, 0
                }

                if (exportEntry.idxLink != 0)
                {
                    InfoTab_PackageLink_ComboBox.SelectedIndex = exportEntry.idxLink + exportEntry.FileRef.Imports.Count; //make positive
                }
                else
                {
                    InfoTab_PackageLink_ComboBox.SelectedIndex = exportEntry.FileRef.Imports.Count; //Class, 0
                }
                InfoTab_Headersize_TextBox.Text = exportEntry.Header.Length + " bytes";
                InfoTab_ObjectnameIndex_TextBox.Text = BitConverter.ToInt32(exportEntry.Header, HEADER_OFFSET_EXP_IDXOBJECTNAME + 4).ToString();
                InfoTab_Archetype_ComboBox.ItemsSource = Classes;
                if (exportEntry.idxArchtype != 0)
                {
                    InfoTab_Archetype_ComboBox.SelectedIndex = exportEntry.idxArchtype + exportEntry.FileRef.Imports.Count; //make positive
                }
                else
                {
                    InfoTab_Archetype_ComboBox.SelectedIndex = exportEntry.FileRef.Imports.Count; //Class, 0
                }
                var flagsList = GetValues<EObjectFlags>().Distinct().ToList();
                //Don't even get me started on how dumb it is that SelectedItems is read only...
                string selectedFlags = "";
                foreach (EObjectFlags flag in flagsList)
                {
                    bool selected = (exportEntry.ObjectFlags & (ulong)flag) != 0;
                    if (selected)
                    {
                        if (selectedFlags != "")
                        {
                            selectedFlags += " ";
                        }
                        selectedFlags += flag;
                    }
                }

                InfoTab_Flags_ComboBox.ItemsSource = flagsList;
                InfoTab_Flags_ComboBox.SelectedValue = selectedFlags;

                InfoTab_ExportDataSize_TextBox.Text = exportEntry.DataSize + " bytes";
                InfoTab_ExportOffsetHex_TextBox.Text = "0x" + exportEntry.DataOffset.ToString("X8");
                InfoTab_ExportOffsetDec_TextBox.Text = exportEntry.DataOffset.ToString();


            }
            catch (Exception e)
            {
                MessageBox.Show("An error occured while attempting to read the header for this export. This indicates there is likely something wrong with the header or its parent header.\n\n" + e.Message);
            }

            CurrentLoadedEntry = exportEntry;
            Header_Hexbox.ByteProvider = new DynamicByteProvider(CurrentLoadedEntry.Header);
            Header_Hexbox.ByteProvider.Changed += InfoTab_Header_ByteProvider_InternalChanged;
            loadingNewData = false;
        }

        public void LoadImport(ImportEntry importEntry)
        {
            loadingNewData = true;
            Info_Header_UnsavedChanges.Visibility = Visibility.Collapsed;
            InfoTab_Headersize_TextBox.Text = importEntry.Header.Length + " bytes";
            Row_Archetype.Height = new GridLength(0);
            Row_ExpClass.Height = new GridLength(0);
            Row_ImpClass.Height = new GridLength(24);
            Row_ExportDataSize.Height = new GridLength(0);
            Row_ExportDataOffsetDec.Height = new GridLength(0);
            Row_ExportDataOffsetHex.Height = new GridLength(0);

            Row_Superclass.Height = new GridLength(0);
            Row_ObjectFlags.Height = new GridLength(0);
            Row_Packagefile.Height = new GridLength(24);
            InfoTab_Link_TextBlock.Text = "0x0C Link:";
            InfoTab_ObjectName_TextBlock.Text = "0x14 Object name:";

            InfoTab_Objectname_ComboBox.SelectedItem = importEntry.ObjectName;
            InfoTab_ImpClass_ComboBox.SelectedItem = importEntry.ClassName;
            if (importEntry.idxLink != 0)
            {
                InfoTab_PackageLink_ComboBox.SelectedIndex = importEntry.idxLink + importEntry.FileRef.Imports.Count; //make positive
            }
            else
            {
                InfoTab_PackageLink_ComboBox.SelectedIndex = importEntry.FileRef.Imports.Count; //Class, 0
            }

            InfoTab_PackageFile_ComboBox.SelectedItem = System.IO.Path.GetFileNameWithoutExtension(importEntry.PackageFile);
            InfoTab_ObjectnameIndex_TextBox.Text = BitConverter.ToInt32(importEntry.Header, HEADER_OFFSET_IMP_IDXOBJECTNAME + 4).ToString();
            CurrentLoadedEntry = importEntry;
            Header_Hexbox.ByteProvider = new DynamicByteProvider(CurrentLoadedEntry.Header);
            Header_Hexbox.ByteProvider.Changed += InfoTab_Header_ByteProvider_InternalChanged;
            loadingNewData = false;
        }

        internal void SetHexboxSelectedOffset(long v)
        {
            if (Header_Hexbox != null)
            {
                Header_Hexbox.SelectionStart = v;
                Header_Hexbox.SelectionLength = 1;
            }
        }

        internal void ClearMetadataPane()
        {
            loadingNewData = true;
            InfoTab_Objectname_ComboBox.SelectedItem = null;
            InfoTab_Class_ComboBox.SelectedItem = null;
            InfoTab_Superclass_ComboBox.SelectedItem = null;
            InfoTab_PackageLink_ComboBox.SelectedItem = null;
            InfoTab_Headersize_TextBox.Text = null;
            InfoTab_ObjectnameIndex_TextBox.Text = null;
            InfoTab_Archetype_ComboBox.ItemsSource = null;
            InfoTab_Archetype_ComboBox.Items.Clear();
            InfoTab_Archetype_ComboBox.SelectedItem = null;
            InfoTab_Flags_ComboBox.ItemsSource = null;
            InfoTab_Flags_ComboBox.SelectedItem = null;
            InfoTab_ExportDataSize_TextBox.Text = null;
            InfoTab_ExportOffsetHex_TextBox.Text = null;
            InfoTab_ExportOffsetDec_TextBox.Text = null;
            Header_Hexbox.ByteProvider = new DynamicByteProvider(new byte[] { });
            loadingNewData = false;
        }


        public override void UnloadExport()
        {
            UnloadEntry();
        }

        private void UnloadEntry()
        {
            Info_Header_UnsavedChanges.Visibility = Visibility.Collapsed;

            CurrentLoadedEntry = null;
        }

        internal void LoadPccData(IMEPackage pcc)
        {
            InfoTab_Objectname_ComboBox.ItemsSource = pcc.Names;
            InfoTab_ImpClass_ComboBox.ItemsSource = pcc.Names;
            InfoTab_PackageFile_ComboBox.ItemsSource = pcc.Names;
            RefreshClassesList(pcc);
        }

        private void PackageEditorWPF_Loaded(object sender, RoutedEventArgs e)
        {
            // Get reference to hexbox winforms control
            Header_Hexbox = (HexBox)Header_Hexbox_Host.Child;
        }

        private void Info_ClassComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!loadingNewData)
            {
                var selectedClassIndex = InfoTab_Class_ComboBox.SelectedIndex;
                var unrealIndex = selectedClassIndex - CurrentLoadedEntry.FileRef.ImportCount;
                Header_Hexbox.ByteProvider.WriteBytes(HEADER_OFFSET_EXP_IDXCLASS, BitConverter.GetBytes(unrealIndex));
            }
        }

        private void InfoTab_Header_ByteProvider_InternalChanged(object sender, EventArgs e)
        {
            Info_Header_UnsavedChanges.Visibility = Header_Hexbox.ByteProvider.HasChanges() ? Visibility.Visible : Visibility.Hidden;
            Header_Hexbox.Refresh();
        }

        private void Info_HeaderHexSaveChanges_Click(object sender, RoutedEventArgs e)
        {
            MemoryStream m = new MemoryStream();
            IByteProvider provider = Header_Hexbox.ByteProvider;
            for (int i = 0; i < provider.Length; i++)
                m.WriteByte(provider.ReadByte(i));
            CurrentLoadedEntry.Header = m.ToArray();
            /*TreeViewEntry tve = GetTreeViewEntryByUIndex(CurrentlyLoadedEntry.UIndex);
            if (tve != null)
            {
                tve.RefreshDisplayName();
            }
            */
            //todo: mvvm-ize this
            if (Header_Hexbox.ByteProvider != null)
            {
                Header_Hexbox.ByteProvider.ApplyChanges();
            }
            Info_Header_UnsavedChanges.Visibility = Header_Hexbox.ByteProvider.HasChanges() ? Visibility.Visible : Visibility.Hidden;
        }

        private void Info_PackageLinkClassComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!loadingNewData)
            {
                var selectedImpExp = InfoTab_PackageLink_ComboBox.SelectedIndex;
                var unrealIndex = selectedImpExp - CurrentLoadedEntry.FileRef.ImportCount;
                Header_Hexbox.ByteProvider.WriteBytes(CurrentLoadedEntry is ExportEntry ? HEADER_OFFSET_EXP_IDXLINK : HEADER_OFFSET_IMP_IDXLINK, BitConverter.GetBytes(unrealIndex));
            }
        }

        private void Info_SuperClassComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!loadingNewData)
            {
                var selectedClassIndex = InfoTab_Superclass_ComboBox.SelectedIndex;
                var unrealIndex = selectedClassIndex - CurrentLoadedEntry.FileRef.ImportCount;
                Header_Hexbox.ByteProvider.WriteBytes(HEADER_OFFSET_EXP_IDXSUPERCLASS, BitConverter.GetBytes(unrealIndex));
            }
        }

        private void Info_ObjectNameComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!loadingNewData)
            {
                var selectedNameIndex = InfoTab_Objectname_ComboBox.SelectedIndex;
                Header_Hexbox.ByteProvider.WriteBytes(CurrentLoadedEntry is ExportEntry ? HEADER_OFFSET_EXP_IDXOBJECTNAME : HEADER_OFFSET_IMP_IDXOBJECTNAME, BitConverter.GetBytes(selectedNameIndex));
            }
        }

        private void Info_IndexTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!loadingNewData)
            {
                int x;
                if (int.TryParse(InfoTab_ObjectnameIndex_TextBox.Text, out x))
                {
                    Header_Hexbox.ByteProvider.WriteBytes(CurrentLoadedEntry is ExportEntry ? HEADER_OFFSET_EXP_IDXOBJECTNAME + 4 : HEADER_OFFSET_IMP_IDXOBJECTNAME + 4, BitConverter.GetBytes(x));
                }
            }
        }

        private void Info_ArchetypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!loadingNewData)
            {
                var selectedArchetTypeIndex = InfoTab_Archetype_ComboBox.SelectedIndex;
                var unrealIndex = selectedArchetTypeIndex - CurrentLoadedEntry.FileRef.ImportCount;
                Header_Hexbox.ByteProvider.WriteBytes(HEADER_OFFSET_EXP_IDXARCHETYPE, BitConverter.GetBytes(selectedArchetTypeIndex));
            }
        }

        private void Info_PackageFileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!loadingNewData)
            {
                var selectedNameIndex = InfoTab_PackageFile_ComboBox.SelectedIndex;
                Header_Hexbox.ByteProvider.WriteBytes(HEADER_OFFSET_IMP_IDXOBJECTNAME, BitConverter.GetBytes(selectedNameIndex));
            }
        }

        private void Info_ImpClassComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!loadingNewData)
            {
                var selectedNameIndex = InfoTab_PackageFile_ComboBox.SelectedIndex;
                Header_Hexbox.ByteProvider.WriteBytes(HEADER_OFFSET_IMP_IDXCLASSNAME, BitConverter.GetBytes(selectedNameIndex));
            }
        }

        private void InfoTab_Objectname_ComboBox_GotFocus(object sender, RoutedEventArgs e)
        {
            Header_Hexbox.SelectionStart = CurrentLoadedEntry is ExportEntry ? HEADER_OFFSET_EXP_IDXOBJECTNAME : HEADER_OFFSET_IMP_IDXOBJECTNAME;
            Header_Hexbox.SelectionLength = 4;
        }

        private void InfoTab_Class_ComboBox_GotFocus(object sender, RoutedEventArgs e)
        {
            Header_Hexbox.SelectionStart = HEADER_OFFSET_EXP_IDXCLASS;
            Header_Hexbox.SelectionLength = 4;
        }

        private void InfoTab_ImpClass_ComboBox_GotFocus(object sender, RoutedEventArgs e)
        {
            Header_Hexbox.SelectionStart = HEADER_OFFSET_IMP_IDXCLASSNAME;
            Header_Hexbox.SelectionLength = 4;
        }

        private void InfoTab_Superclass_ComboBox_GotFocus(object sender, RoutedEventArgs e)
        {
            Header_Hexbox.SelectionStart = HEADER_OFFSET_EXP_IDXSUPERCLASS;
            Header_Hexbox.SelectionLength = 4;
        }

        private void InfoTab_PackageLink_ComboBox_GotFocus(object sender, RoutedEventArgs e)
        {
            Header_Hexbox.SelectionStart = CurrentLoadedEntry is ExportEntry ? HEADER_OFFSET_EXP_IDXLINK : HEADER_OFFSET_IMP_IDXLINK;
            Header_Hexbox.SelectionLength = 4;
        }

        private void InfoTab_PackageFile_ComboBox_GotFocus(object sender, RoutedEventArgs e)
        {
            Header_Hexbox.SelectionStart = HEADER_OFFSET_IMP_IDXPACKAGEFILE;
            Header_Hexbox.SelectionLength = 4;
        }

        private void InfoTab_Archetype_ComboBox_GotFocus(object sender, RoutedEventArgs e)
        {
            Header_Hexbox.SelectionStart = HEADER_OFFSET_EXP_IDXARCHETYPE;
            Header_Hexbox.SelectionLength = 4;
        }

        private void InfoTab_Flags_ComboBox_GotFocus(object sender, RoutedEventArgs e)
        {
            Header_Hexbox.SelectionStart = HEADER_OFFSET_EXP_OBJECTFLAGS;
            Header_Hexbox.SelectionLength = 8;
        }

        private void InfoTab_ObjectNameIndex_ComboBox_GotFocus(object sender, RoutedEventArgs e)
        {
            Header_Hexbox.SelectionStart = CurrentLoadedEntry is ExportEntry ? HEADER_OFFSET_EXP_IDXOBJECTNAME + 4 : HEADER_OFFSET_IMP_IDXOBJECTNAME + 4;
            Header_Hexbox.SelectionLength = 4;
        }

        /// <summary>
        /// Handler for when the flags combobox item changes value
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void InfoTab_Flags_ComboBox_ItemSelectionChanged(object sender, Xceed.Wpf.Toolkit.Primitives.ItemSelectionChangedEventArgs e)
        {
            if (!loadingNewData)
            {
                EPropertyFlags newFlags = 0U;
                foreach (var flag in InfoTab_Flags_ComboBox.Items)
                {
                    var selectorItem = InfoTab_Flags_ComboBox.ItemContainerGenerator.ContainerFromItem(flag) as SelectorItem;
                    if ((selectorItem != null) && !selectorItem.IsSelected.Value)
                    {
                        newFlags |= (EPropertyFlags)flag;
                    }
                }
                //Debug.WriteLine(newFlags);
                Header_Hexbox.ByteProvider.WriteBytes(HEADER_OFFSET_EXP_OBJECTFLAGS, BitConverter.GetBytes((UInt64)newFlags));
            }
        }

        private void MetadataEditor_Loaded(object sender, RoutedEventArgs e)
        {
            Header_Hexbox = (HexBox)Header_Hexbox_Host.Child;
        }
    }
}
