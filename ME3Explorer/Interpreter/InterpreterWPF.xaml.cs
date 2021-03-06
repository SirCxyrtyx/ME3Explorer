﻿using ME1Explorer.Unreal;
using ME1Explorer.Unreal.Classes;
using ME2Explorer.Unreal;
using ME3Explorer.Packages;
using ME3Explorer.Unreal;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Media;
using System.IO;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Be.Windows.Forms;
using System.Windows;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ME3Explorer.SharedUI;
using System.Windows.Input;

namespace ME3Explorer
{
    /// <summary>
    /// Interaction logic for InterpreterWPF.xaml
    /// </summary>
    public partial class InterpreterWPF : ExportLoaderControl
    {
        public ObservableCollectionExtended<UPropertyTreeViewEntry> PropertyNodes { get; set; } = new ObservableCollectionExtended<UPropertyTreeViewEntry>();

        //Values in this list will cause the ExportToString() method to be called on an objectproperty in InterpreterWPF.
        //This is useful for end user when they want to view things in a list for example, but all of the items are of the 
        //same type and are not distinguishable without changing to another export, wasting a lot of time.
        public static readonly string[] ExportToStringConverters = { "LevelStreamingKismet" };
        public static readonly string[] IntToStringConverters = { "WwiseEvent" };

        /// <summary>
        /// The current list of loaded properties that back the property tree.
        /// This is used so we can do direct object reference comparisons for things like removal
        /// </summary>
        private PropertyCollection CurrentLoadedProperties;
        //Values in this list will cause custom code to be fired to modify what the displayed string is for IntProperties
        //when the class matches.

        private Dictionary<string, List<PropertyReader.Property>> defaultStructValues;
        //private byte[] memory;
        //private int memsize;
        private BioTlkFileSet tlkset;
        private BioTlkFileSet editorTlkSet;
        int readerpos;
        int RescanSelectionOffset = 0;
        private List<FrameworkElement> EditorSetElements = new List<FrameworkElement>();
        public struct PropHeader
        {
            public int name;
            public int type;
            public int size;
            public int index;
            public int offset;
        }

        public string[] Types =
        {
            "StructProperty", //0
            "IntProperty",
            "FloatProperty",
            "ObjectProperty",
            "NameProperty",
            "BoolProperty",  //5
            "ByteProperty",
            "ArrayProperty",
            "StrProperty",
            "StringRefProperty",
            "DelegateProperty",//10
            "None",
            "BioMask4Property",
        };
        private HexBox Interpreter_Hexbox;

        public enum nodeType
        {
            Unknown = -1,
            StructProperty = 0,
            IntProperty = 1,
            FloatProperty = 2,
            ObjectProperty = 3,
            NameProperty = 4,
            BoolProperty = 5,
            ByteProperty = 6,
            ArrayProperty = 7,
            StrProperty = 8,
            StringRefProperty = 9,
            DelegateProperty = 10,
            None,
            BioMask4Property,

            ArrayLeafObject,
            ArrayLeafName,
            ArrayLeafEnum,
            ArrayLeafStruct,
            ArrayLeafBool,
            ArrayLeafString,
            ArrayLeafFloat,
            ArrayLeafInt,
            ArrayLeafByte,

            StructLeafByte,
            StructLeafFloat,
            StructLeafDeg, //indicates this is a StructProperty leaf that is in degrees (actually unreal rotation units)
            StructLeafInt,
            StructLeafObject,
            StructLeafName,
            StructLeafBool,
            StructLeafStr,
            StructLeafArray,
            StructLeafEnum,
            StructLeafStruct,

            Root,
        }

        public InterpreterWPF()
        {
            LoadCommands();
            InitializeComponent();
            EditorSetElements.Add(Value_TextBox); //str, strref, int, float, obj
            EditorSetElements.Add(Value_ComboBox); //bool, name
            EditorSetElements.Add(NameIndexPrefix_TextBlock); //nameindex
            EditorSetElements.Add(NameIndex_TextBox); //nameindex
            EditorSetElements.Add(ParsedValue_TextBlock);
            EditorSetElements.Add(AddArrayElement_Button);
            EditorSetElements.Add(RemoveArrayElement_Button);
            EditorSetElements.Add(EditorSet_ArraySetSeparator);
            Set_Button.Visibility = Visibility.Collapsed;
            EditorSet_Separator.Visibility = Visibility.Collapsed;
            defaultStructValues = new Dictionary<string, List<PropertyReader.Property>>();
        }

        #region Commands
        public ICommand RemovePropertyCommand { get; set; }
        public ICommand AddPropertyCommand { get; set; }
        public ICommand CollapseChildrenCommand { get; set; }
        public ICommand ExpandChildrenCommand { get; set; }
        public ICommand SortChildrenCommand { get; set; }
        public ICommand SortParsedArrayAscendingCommand { get; set; } //obj, name only
        public ICommand SortParsedArrayDescendingCommand { get; set; } //obj, name only
        public ICommand SortValueArrayAscendingCommand { get; set; }
        public ICommand SortValueArrayDescendingCommand { get; set; }

        private void LoadCommands()
        {
            RemovePropertyCommand = new RelayCommand(RemoveProperty, CanRemoveProperty);
            AddPropertyCommand = new RelayCommand(AddProperty, CanAddProperty);
            CollapseChildrenCommand = new RelayCommand(CollapseChildren, CanExpandOrCollapseChildren);
            ExpandChildrenCommand = new RelayCommand(ExpandChildren, CanExpandOrCollapseChildren);
            SortChildrenCommand = new RelayCommand(SortChildren, CanExpandOrCollapseChildren);

            SortParsedArrayAscendingCommand = new RelayCommand(SortParsedArrayAscending, CanSortArrayPropByParsedValue);
            SortParsedArrayDescendingCommand = new RelayCommand(SortParsedArrayDescending, CanSortArrayPropByParsedValue);
            SortValueArrayAscendingCommand = new RelayCommand(SortValueArrayAscending, CanSortArrayPropByValue);
            SortValueArrayDescendingCommand = new RelayCommand(SortValueArrayDescending, CanSortArrayPropByValue);

        }

        private void SortParsedArrayAscending(object obj)
        {
            UPropertyTreeViewEntry tvi = (UPropertyTreeViewEntry)Interpreter_TreeView.SelectedItem;
            if (tvi != null && tvi.Property != null)
            {
                SortArrayPropertyParsed(tvi.Property, true);
            }
        }

        private void SortValueArrayAscending(object obj)
        {
            UPropertyTreeViewEntry tvi = (UPropertyTreeViewEntry)Interpreter_TreeView.SelectedItem;
            if (tvi != null && tvi.Property != null)
            {
                SortArrayPropertyValue(tvi.Property, true);
            }
        }

        private void SortValueArrayDescending(object obj)
        {
            UPropertyTreeViewEntry tvi = (UPropertyTreeViewEntry)Interpreter_TreeView.SelectedItem;
            if (tvi != null && tvi.Property != null)
            {
                SortArrayPropertyValue(tvi.Property, false);
            }
        }

        private void SortParsedArrayDescending(object obj)
        {
            UPropertyTreeViewEntry tvi = (UPropertyTreeViewEntry)Interpreter_TreeView.SelectedItem;
            if (tvi != null && tvi.Property != null)
            {
                SortArrayPropertyParsed(tvi.Property, false);
            }
        }

        private void SortArrayPropertyValue(UProperty property, bool ascending)
        {
            switch (property)
            {
                case ArrayProperty<ObjectProperty> aop:
                    aop.Values = ascending ? aop.OrderBy(x => x.Value).ToList() : aop.OrderByDescending(x => x.Value).ToList();
                    break;
                case ArrayProperty<IntProperty> aip:
                    aip.Values = ascending ? aip.OrderBy(x => x.Value).ToList() : aip.OrderByDescending(x => x.Value).ToList();
                    break;
                case ArrayProperty<FloatProperty> afp:
                    afp.Values = ascending ? afp.OrderBy(x => x.Value).ToList() : afp.OrderByDescending(x => x.Value).ToList();
                    break;
            }
            CurrentLoadedExport.WriteProperties(CurrentLoadedProperties);
        }

        private void SortArrayPropertyParsed(UProperty property, bool ascending)
        {
            switch (property)
            {
                case ArrayProperty<ObjectProperty> aop:
                    //todo: order by index too in thenby()
                    aop.Values = ascending ? aop.OrderBy(x => CurrentLoadedExport.FileRef.getEntry(x.Value).GetFullPath).ToList() : aop.OrderByDescending(x => CurrentLoadedExport.FileRef.getEntry(x.Value).GetFullPath).ToList();
                    break;
                case ArrayProperty<NameProperty> anp:
                    anp.Values = ascending ? anp.OrderBy(x => x.Value.Name).ThenBy(x => x.Value.Number).ToList() : anp.OrderByDescending(x => x.Value.Name).ThenBy(x => x.Value.Number).ToList();
                    break;
            }
            CurrentLoadedExport.WriteProperties(CurrentLoadedProperties);
        }

        internal void SetHexboxSelectedOffset(int v)
        {
            if (Interpreter_Hexbox != null)
            {
                Interpreter_Hexbox.SelectionStart = v;
                Interpreter_Hexbox.SelectionLength = 1;
            }
        }

        private bool CanSortArrayPropByParsedValue(object obj)
        {
            UPropertyTreeViewEntry tvi = (UPropertyTreeViewEntry)Interpreter_TreeView.SelectedItem;
            return tvi != null && tvi.Property != null && (
                tvi.Property is ArrayProperty<NameProperty> ||
                tvi.Property is ArrayProperty<ObjectProperty>
                );
        }

        private bool CanSortArrayPropByValue(object obj)
        {
            UPropertyTreeViewEntry tvi = (UPropertyTreeViewEntry)Interpreter_TreeView.SelectedItem;
            return tvi != null && tvi.Property != null && (
                tvi.Property is ArrayProperty<NameProperty> ||
                tvi.Property is ArrayProperty<ObjectProperty> ||
                tvi.Property is ArrayProperty<IntProperty> ||
                tvi.Property is ArrayProperty<FloatProperty>
                );
        }

        private void SortChildren(object obj)
        {
            UPropertyTreeViewEntry tvi = (UPropertyTreeViewEntry)Interpreter_TreeView.SelectedItem;
            tvi.ChildrenProperties.Sort(x => x.Property.Name.Name);
        }

        private void ExpandChildren(object obj)
        {
            UPropertyTreeViewEntry tvi = (UPropertyTreeViewEntry)Interpreter_TreeView.SelectedItem;
            if (tvi != null)
            {
                SetChildrenExpandedStateRecursive(tvi, true);
            }
        }

        private void SetChildrenExpandedStateRecursive(UPropertyTreeViewEntry root, bool IsExpanded)
        {
            root.IsExpanded = IsExpanded;
            foreach (UPropertyTreeViewEntry tvi in root.ChildrenProperties)
            {
                SetChildrenExpandedStateRecursive(tvi, IsExpanded);
            }
        }

        private bool CanExpandOrCollapseChildren(object obj)
        {
            UPropertyTreeViewEntry tvi = (UPropertyTreeViewEntry)Interpreter_TreeView.SelectedItem;
            return tvi != null && tvi.ChildrenProperties.Count > 0;
        }

        private void CollapseChildren(object obj)
        {
            UPropertyTreeViewEntry tvi = (UPropertyTreeViewEntry)Interpreter_TreeView.SelectedItem;
            if (tvi != null)
            {
                SetChildrenExpandedStateRecursive(tvi, false);
            }
        }

        private bool CanAddProperty(object obj)
        {
            if (CurrentLoadedExport == null)
            {
                return false;
            }
            if (CurrentLoadedExport.FileRef.Game == MEGame.UDK)
            {
                //MessageBox.Show("Cannot add properties to UDK UPK files.", "Unsupported operation");
                return false;
            }
            if (CurrentLoadedExport.ClassName == "Class")
            {
                return false; //you can't add properties to class objects.
                //we might want to see if the export has a NoneProperty - if it doesn't, adding properties won't work either.
                //TODO
            }

            return true;
        }

        private void AddProperty(object obj)
        {
            if (CurrentLoadedExport.FileRef.Game == MEGame.UDK)
            {
                MessageBox.Show("Cannot add properties to UDK UPK files.", "Unsupported operation");
                return;
            }

            List<string> props = new List<string>();
            foreach (UProperty cProp in CurrentLoadedProperties)
            {
                //build a list we are going to the add dialog
                props.Add(cProp.Name);
            }

            Tuple<string, PropertyInfo> prop = AddPropertyDialogWPF.GetProperty(CurrentLoadedExport, props, CurrentLoadedExport.FileRef.Game, Window.GetWindow(this));

            if (prop != null)
            {
                //not sure if the following is actually needed anymore from interpreter classic

                /*string origname = CurrentLoadedExport.ClassName;
                string temp = CurrentLoadedExport.ClassName;
                List<string> classes = new List<string>();
                Dictionary<string, ClassInfo> classList;
                switch (CurrentLoadedExport.FileRef.Game)
                {
                    case MEGame.ME1:
                        classList = ME1Explorer.Unreal.ME1UnrealObjectInfo.Classes;
                        break;
                    case MEGame.ME2:
                        classList = ME2Explorer.Unreal.ME2UnrealObjectInfo.Classes;
                        break;
                    case MEGame.ME3:
                    default:
                        classList = ME3UnrealObjectInfo.Classes;
                        break;
                }
                ClassInfo currentInfo = null;
                if (!classList.ContainsKey(temp))
                {
                    IExportEntry exportTemp = CurrentLoadedExport.FileRef.Exports[CurrentLoadedExport.idxClass - 1];
                    //current object is not in classes db, temporarily add it to the list
                    switch (CurrentLoadedExport.FileRef.Game)
                    {
                        case MEGame.ME1:
                            currentInfo = ME1Explorer.Unreal.ME1UnrealObjectInfo.generateClassInfo(exportTemp);
                            break;
                        case MEGame.ME2:
                            currentInfo = ME2Explorer.Unreal.ME2UnrealObjectInfo.generateClassInfo(exportTemp);
                            break;
                        case MEGame.ME3:
                        default:
                            currentInfo = ME3UnrealObjectInfo.generateClassInfo(exportTemp);
                            break;
                    }
                    currentInfo.baseClass = exportTemp.ClassParent;
                }*/

                UProperty newProperty = null;
                //Todo: Maybe lookup the default value?
                switch (prop.Item2.type)
                {
                    case PropertyType.IntProperty:
                        newProperty = new IntProperty(0, prop.Item1);
                        break;
                    case PropertyType.BoolProperty:
                        newProperty = new BoolProperty(false, prop.Item1);
                        break;
                    case PropertyType.FloatProperty:
                        newProperty = new FloatProperty(0.0f, prop.Item1);
                        break;
                    case PropertyType.StringRefProperty:
                        newProperty = new StringRefProperty(prop.Item1);
                        break;
                    case PropertyType.StrProperty:
                        newProperty = new StrProperty("", prop.Item1);
                        break;
                    case PropertyType.ArrayProperty:
                        newProperty = new ArrayProperty<IntProperty>(ArrayType.Int, prop.Item1); //We can just set it to int as it will be reparsed and resolved.
                        break;
                    case PropertyType.StructProperty:
                        // Generate the bytecode and then read it as a prop.
                        // This is effectively the way classic interpreter does it
                        // and I have to use it since I don't have a way to just
                        // get UProperty struct
                        List<byte> buff = new List<byte>();
                        //name
                        buff.AddRange(BitConverter.GetBytes(CurrentLoadedExport.FileRef.FindNameOrAdd(prop.Item1)));
                        buff.AddRange(new byte[4]);
                        //type
                        buff.AddRange(BitConverter.GetBytes(CurrentLoadedExport.FileRef.FindNameOrAdd(prop.Item2.type.ToString())));
                        buff.AddRange(new byte[4]);
                        byte[] structBuff = ME3UnrealObjectInfo.getDefaultClassValue(CurrentLoadedExport.FileRef as ME3Package, prop.Item2.reference);
                        //struct length
                        buff.AddRange(BitConverter.GetBytes(structBuff.Length));
                        buff.AddRange(new byte[4]);
                        //struct Type
                        buff.AddRange(BitConverter.GetBytes(CurrentLoadedExport.FileRef.FindNameOrAdd(prop.Item2.reference)));
                        buff.AddRange(new byte[4]);
                        buff.AddRange(structBuff);
                        structBuff = buff.ToArray();
                        //read data to get property. We set includeNonePRoperty because this buff only has one property - true ones will have at least 2 (the single prop, and None). So we don't want to clip off our property
                        var structPropertyGenerated = PropertyCollection.ReadProps(CurrentLoadedExport.FileRef, new MemoryStream(structBuff), CurrentLoadedExport.ClassName, includeNoneProperty: true, requireNoneAtEnd: false);
                        newProperty = structPropertyGenerated[0]; //i'm sure i won't regret this later
                        break;
                }

                //UProperty property = generateNewProperty(prop.Item1, currentInfo);
                if (newProperty != null)
                {
                    CurrentLoadedProperties.Insert(CurrentLoadedProperties.Count - 1, newProperty); //insert before noneproperty
                }
                //Todo: Create new node, prevent refresh of this instance.
                CurrentLoadedExport.WriteProperties(CurrentLoadedProperties);
                //End Todo
            }
        }

        private void RemoveProperty(object obj)
        {
            UPropertyTreeViewEntry tvi = (UPropertyTreeViewEntry)Interpreter_TreeView.SelectedItem;
            if (tvi != null && tvi.Property != null)
            {
                UProperty tag = tvi.Property;
                CurrentLoadedProperties.Remove(tag);
                CurrentLoadedExport.WriteProperties(CurrentLoadedProperties);
                StartScan();
            }
        }

        private bool CanRemoveProperty(object obj)
        {
            UPropertyTreeViewEntry tvi = (UPropertyTreeViewEntry)Interpreter_TreeView.SelectedItem;
            if (tvi != null)
            {
                return tvi.Parent != null && tvi.Parent.Parent == null && !(tvi.Property is NoneProperty); //only items with a single parent (root nodes)
            }
            return false;
        }

        #endregion

        /// <summary>
        /// Unloads the loaded export, if any
        /// </summary>
        public override void UnloadExport()
        {
            CurrentLoadedExport = null;
            EditorSetElements.ForEach(x => x.Visibility = Visibility.Collapsed);
            Set_Button.Visibility = Visibility.Collapsed;
            EditorSet_Separator.Visibility = Visibility.Collapsed;
            Interpreter_Hexbox.ByteProvider = new DynamicByteProvider(new byte[] { });
            PropertyNodes.Clear();
        }

        /// <summary>
        /// Load a new export for display and editing in this control
        /// </summary>
        /// <param name="export"></param>
        public override void LoadExport(IExportEntry export)
        {
            EditorSetElements.ForEach(x => x.Visibility = Visibility.Collapsed);

            //set rescan offset
            //TODO: Make this more reliable because it is recycling virtualization
            if (CurrentLoadedExport != null && export.FileRef == CurrentLoadedExport.FileRef && export.UIndex == CurrentLoadedExport.UIndex)
            {
                UPropertyTreeViewEntry tvi = (UPropertyTreeViewEntry)Interpreter_TreeView.SelectedItem;
                if (tvi != null && tvi.Property != null)
                {
                    RescanSelectionOffset = (int)tvi.Property.ValueOffset;
                }
            }
            else
            {
                RescanSelectionOffset = 0;
            }
            CurrentLoadedExport = export;
            Interpreter_Hexbox.ByteProvider = new DynamicByteProvider(export.Data);

            if (CurrentLoadedExport.FileRef.Game == MEGame.ME1)
            {
                // attempt to find a TlkFileSet associated with the object, else just pick the first one and hope it's correct
                if (editorTlkSet == null)
                {
                    try
                    {
                        IntProperty tlkSetRef = export.GetProperty<IntProperty>("m_oTlkFileSet");
                        if (tlkSetRef != null)
                        {
                            tlkset = new BioTlkFileSet(CurrentLoadedExport.FileRef as ME1Package, tlkSetRef.Value - 1);
                        }
                        else
                        {
                            tlkset = new BioTlkFileSet(CurrentLoadedExport.FileRef as ME1Package);
                        }
                    }
                    catch (Exception e)
                    {
                        tlkset = new BioTlkFileSet(CurrentLoadedExport.FileRef as ME1Package);
                    }
                }
                else
                {
                    tlkset = editorTlkSet;
                }
            }
            StartScan();
        }

        /// <summary>
        /// Call this when reloading the entire tree.
        /// </summary>
        /// <param name="expandedItems"></param>
        /// <param name="topNodeName"></param>
        /// <param name="selectedNodeName"></param>
        private void StartScan(IEnumerable<string> expandedItems = null, string topNodeName = null, string selectedNodeName = null)
        {
            PropertyNodes.Clear();
            readerpos = CurrentLoadedExport.GetPropertyStart();

            if (CurrentLoadedExport.ClassName == "Class")
            {
                UPropertyTreeViewEntry topLevelTree = new UPropertyTreeViewEntry()
                {
                    DisplayName = $"Export {CurrentLoadedExport.UIndex }: { CurrentLoadedExport.ObjectName} ({CurrentLoadedExport.ClassName})",
                    IsExpanded = true
                };

                topLevelTree.ChildrenProperties.Add(new UPropertyTreeViewEntry()
                {
                    DisplayName = "Class objects do not have properties",
                    Property = new UnknownProperty(),
                    Parent = topLevelTree
                });

                PropertyNodes.Add(topLevelTree);
            }
            else
            {

                UPropertyTreeViewEntry topLevelTree = new UPropertyTreeViewEntry()
                {
                    DisplayName = $"Export {CurrentLoadedExport.UIndex }: { CurrentLoadedExport.ObjectName} ({CurrentLoadedExport.ClassName})",
                    IsExpanded = true
                };

                PropertyNodes.Add(topLevelTree);

                try
                {
                    CurrentLoadedProperties = CurrentLoadedExport.GetProperties(includeNoneProperties: true);
                    foreach (UProperty prop in CurrentLoadedProperties)
                    {
                        GenerateUPropertyTreeForProperty(prop, topLevelTree, CurrentLoadedExport);
                    }
                }
                catch (Exception ex)
                {
                    UPropertyTreeViewEntry errorNode = new UPropertyTreeViewEntry()
                    {
                        DisplayName = $"PARSE ERROR {ex.Message}"
                    };
                    topLevelTree.ChildrenProperties.Add(errorNode);
                }

                if (RescanSelectionOffset != 0)
                {
                    var flattenedTree = topLevelTree.FlattenTree();
                    var itemToSelect = flattenedTree.FirstOrDefault(x => x.Property != null && x.Property.ValueOffset == RescanSelectionOffset);
                    if (itemToSelect != null)
                    {
                        //todo: select node using parent-first selection (from packageeditorwpf)
                        //due to tree view virtualization
                        itemToSelect.ExpandParents();
                        itemToSelect.IsSelected = true;
                    }
                }
            }
        }


        #region Static tree generating code (shared with BinaryInterpreterWPF)
        public static void GenerateUPropertyTreeForProperty(UProperty prop, UPropertyTreeViewEntry parent, IExportEntry export, string displayPrefix = "")
        {
            var upropertyEntry = GenerateUPropertyTreeViewEntry(prop, parent, export, displayPrefix);
            if (prop.PropType == PropertyType.ArrayProperty)
            {
                int i = 0;
                foreach (UProperty listProp in (prop as ArrayPropertyBase).ValuesAsProperties)
                {
                    GenerateUPropertyTreeForProperty(listProp, upropertyEntry, export, $" Item {i++}:");
                }
            }
            if (prop.PropType == PropertyType.StructProperty)
            {
                var sProp = prop as StructProperty;
                foreach (var subProp in sProp.Properties)
                {
                    GenerateUPropertyTreeForProperty(subProp, upropertyEntry, export);
                }
            }
        }

        public static UPropertyTreeViewEntry GenerateUPropertyTreeViewEntry(UProperty prop, UPropertyTreeViewEntry parent, IExportEntry parsingExport, string displayPrefix = "")
        {
            string displayName = $"{prop.StartOffset.ToString("X4")}{displayPrefix}";
            if (parent.Property == null || !parent.Property.GetType().IsOfGenericType(typeof(ArrayProperty<>)))
            {
                displayName += $": { prop.Name}:";
            }
            string editableValue = ""; //editable value
            string parsedValue = ""; //human formatted item. Will most times be blank
            switch (prop)
            {
                case ObjectProperty op:
                    {
                        int index = op.Value;
                        var entry = parsingExport.FileRef.getEntry(index);
                        if (entry != null)
                        {
                            editableValue = index.ToString();
                            parsedValue = entry.GetFullPath;
                            if (index > 0 && ExportToStringConverters.Contains(entry.ClassName))
                            {
                                editableValue += " " + ExportToString(parsingExport.FileRef.Exports[index - 1]);
                            }
                        }
                        else if (index == 0)
                        {
                            editableValue = index.ToString();
                            parsedValue = "Null";

                        }
                        else
                        {
                            editableValue = index.ToString();
                            parsedValue = "Index out of bounds of " + (index < 0 ? "Import" : "Export") + " list";
                        }
                    }
                    break;
                case IntProperty ip:
                    {
                        editableValue = ip.Value.ToString();
                        if (IntToStringConverters.Contains(parsingExport.ClassName))
                        {
                            parsedValue = IntToString(prop.Name, ip.Value, parsingExport);
                        }
                        if (parent.Property is StructProperty && (parent.Property as StructProperty).StructType == "Rotator")
                        {
                            parsedValue = "(" + (ip.Value * 360f / 65536f).ToString("0.0######") + " degrees)";
                        }
                    }
                    break;
                case FloatProperty fp:
                    {
                        editableValue = fp.Value.ToString();
                    }
                    break;
                case BoolProperty bp:
                    {
                        editableValue = bp.Value.ToString(); //combobox
                    }
                    break;
                case ArrayPropertyBase ap:
                    {
                        //todo - assign bottom text to show array type.
                        ArrayType at = GetArrayType(prop.Name.Name, parsingExport);
                        editableValue = $"{at.ToString()} array";
                    }
                    break;
                case NameProperty np:
                    editableValue = parsingExport.FileRef.findName(np.Value.Name.ToString()) + "_" + np.Value.Number.ToString();
                    parsedValue = np.Value + "_" + np.Value.Number.ToString(); //will require special 2-box setup
                    break;
                case ByteProperty bp:
                    editableValue = (prop as ByteProperty).Value.ToString();
                    parsedValue = (prop as ByteProperty).Value.ToString();

                    break;
                case EnumProperty ep:
                    //editableValue = (prop as EnumProperty).Value.ToString();
                    editableValue = (prop as EnumProperty).Value;
                    break;
                case StringRefProperty strrefp:
                    editableValue = strrefp.Value.ToString();
                    if (parsingExport.FileRef.Game == MEGame.ME3)
                    {
                        parsedValue = ME3TalkFiles.tlkList.Count == 0 ? "(.tlk not loaded)" : ME3TalkFiles.findDataById(strrefp.Value);
                    }
                    break;
                case StrProperty strp:
                    editableValue = strp.Value;
                    break;
                case StructProperty sp:
                    parsedValue = sp.StructType;
                    break;
                case NoneProperty np:
                    parsedValue = "End of properties";
                    break;
            }
            UPropertyTreeViewEntry item = new UPropertyTreeViewEntry()
            {
                Property = prop,
                EditableValue = editableValue,
                ParsedValue = parsedValue,
                DisplayName = displayName,
                Parent = parent,
            };
            //item.PropertyUpdated += OnPropertyUpdated;
            parent.ChildrenProperties.Add(item);
            return item;
        }

        /// <summary>
        /// Converts a value of a property into a more human readable string.
        /// This is for IntProperty.
        /// </summary>
        /// <param name="name">Name of the property</param>
        /// <param name="value">Value of the property to transform</param>
        /// <returns></returns>
        private static string IntToString(NameReference name, int value, IExportEntry export)
        {
            switch (export.ClassName)
            {
                case "WwiseEvent":
                    switch (name)
                    {
                        case "Id":
                            return " (0x" + value.ToString("X8") + ")";
                    }
                    break;

            }
            return "";
        }

        private static string ExportToString(IExportEntry exportEntry)
        {
            switch (exportEntry.ObjectName)
            {
                case "LevelStreamingKismet":
                    NameProperty prop = exportEntry.GetProperty<NameProperty>("PackageName");
                    return "(" + prop.Value.Name + "_" + prop.Value.Number + ")";
            }
            return "";
        }

        #endregion
        /// <summary>
        /// This handler is assigned to UPropertyTreeViewEntry and is called
        /// when the value stored by the entry is updated.
        /// </summary>
        /// <param name="sender">Calling UPropertyTreeViewEntry object</param>
        /// <param name="e"></param>
        private void OnPropertyUpdated(object sender, EventArgs e)
        {
            Debug.WriteLine("prop updated");
            UPropertyTreeViewEntry Sender = sender as UPropertyTreeViewEntry;
            if (Sender != null && Sender.Property != null)
            {
                int offset = (int)Sender.Property.ValueOffset;

                switch (Sender.Property.PropType)
                {
                    case PropertyType.IntProperty:
                        Interpreter_Hexbox.ByteProvider.WriteBytes(offset, BitConverter.GetBytes((Sender.Property as IntProperty).Value));
                        break;
                    case PropertyType.FloatProperty:
                        Interpreter_Hexbox.ByteProvider.WriteBytes(offset, BitConverter.GetBytes((Sender.Property as FloatProperty).Value));
                        break;
                }
                Interpreter_Hexbox.Refresh();
            }
        }

        private void hb1_SelectionChanged(object sender, EventArgs e)
        {
            int start = (int)Interpreter_Hexbox.SelectionStart;
            int len = (int)Interpreter_Hexbox.SelectionLength;
            int size = (int)Interpreter_Hexbox.ByteProvider.Length;
            //TODO: Optimize this so this is only called when data has changed
            byte[] currentData = (Interpreter_Hexbox.ByteProvider as DynamicByteProvider).Bytes.ToArray();
            try
            {
                if (currentData != null && start != -1 && start < size)
                {
                    string s = $"Byte: {currentData[start]}"; //if selection is same as size this will crash.
                    if (start <= currentData.Length - 4)
                    {
                        int val = BitConverter.ToInt32(currentData, start);
                        s += $", Int: {val}";
                        if (CurrentLoadedExport.FileRef.isName(val))
                        {
                            s += $", Name: {CurrentLoadedExport.FileRef.getNameEntry(val)}";
                        }
                        if (CurrentLoadedExport.FileRef.getEntry(val) is IExportEntry exp)
                        {
                            s += $", Export: {exp.ObjectName}";
                        }
                        else if (CurrentLoadedExport.FileRef.getEntry(val) is ImportEntry imp)
                        {
                            s += $", Import: {imp.ObjectName}";
                        }
                    }
                    s += $" | Start=0x{start.ToString("X8")} ";
                    if (len > 0)
                    {
                        s += $"Length=0x{len.ToString("X8")} ";
                        s += $"End=0x{(start + len - 1).ToString("X8")}";
                    }
                    StatusBar_LeftMostText.Text = s;
                }
                else
                {
                    StatusBar_LeftMostText.Text = "Nothing Selected";
                }
            }
            catch (Exception)
            {
            }
        }

        #region UnrealObjectInfo
        private PropertyInfo GetPropertyInfo(int propName)
        {
            switch (CurrentLoadedExport.FileRef.Game)
            {
                case MEGame.ME1:
                    return ME1UnrealObjectInfo.getPropertyInfo(CurrentLoadedExport.ClassName, CurrentLoadedExport.FileRef.getNameEntry(propName));
                case MEGame.ME2:
                    return ME2UnrealObjectInfo.getPropertyInfo(CurrentLoadedExport.ClassName, CurrentLoadedExport.FileRef.getNameEntry(propName));
                case MEGame.ME3:
                case MEGame.UDK:
                    return ME3UnrealObjectInfo.getPropertyInfo(CurrentLoadedExport.ClassName, CurrentLoadedExport.FileRef.getNameEntry(propName));
            }
            return null;
        }

        private PropertyInfo GetPropertyInfo(string propname, string typeName, bool inStruct = false, ClassInfo nonVanillaClassInfo = null)
        {
            switch (CurrentLoadedExport.FileRef.Game)
            {
                case MEGame.ME1:
                    return ME1UnrealObjectInfo.getPropertyInfo(typeName, propname, inStruct, nonVanillaClassInfo);
                case MEGame.ME2:
                    return ME2UnrealObjectInfo.getPropertyInfo(typeName, propname, inStruct, nonVanillaClassInfo);
                case MEGame.ME3:
                case MEGame.UDK:
                    return ME3UnrealObjectInfo.getPropertyInfo(typeName, propname, inStruct, nonVanillaClassInfo);
            }
            return null;
        }

        private static ArrayType GetArrayType(PropertyInfo propInfo, IMEPackage pcc)
        {
            switch (pcc.Game)
            {
                case MEGame.ME1:
                    return ME1UnrealObjectInfo.getArrayType(propInfo);
                case MEGame.ME2:
                    return ME2UnrealObjectInfo.getArrayType(propInfo);
                case MEGame.ME3:
                case MEGame.UDK:
                    return ME3UnrealObjectInfo.getArrayType(propInfo);
            }
            return ArrayType.Int;
        }

        private static ArrayType GetArrayType(string propName, IExportEntry parsingExport, string typeName = null)
        {
            if (typeName == null)
            {
                typeName = parsingExport.ClassName;
            }
            switch (parsingExport.FileRef.Game)
            {
                case MEGame.ME1:
                    return ME1UnrealObjectInfo.getArrayType(typeName, propName, export: parsingExport);
                case MEGame.ME2:
                    return ME2UnrealObjectInfo.getArrayType(typeName, propName, export: parsingExport);
                case MEGame.ME3:
                case MEGame.UDK:
                    return ME3UnrealObjectInfo.getArrayType(typeName, propName, export: parsingExport);
            }
            return ArrayType.Int;
        }

        private ArrayType GetArrayType(int propName, string typeName = null)
        {
            if (typeName == null)
            {
                typeName = CurrentLoadedExport.ClassName;
            }
            switch (CurrentLoadedExport.FileRef.Game)
            {
                case MEGame.ME1:
                    return ME1UnrealObjectInfo.getArrayType(typeName, CurrentLoadedExport.FileRef.getNameEntry(propName), export: CurrentLoadedExport);
                case MEGame.ME2:
                    return ME2UnrealObjectInfo.getArrayType(typeName, CurrentLoadedExport.FileRef.getNameEntry(propName), export: CurrentLoadedExport);
                case MEGame.ME3:
                case MEGame.UDK:
                    return ME3UnrealObjectInfo.getArrayType(typeName, CurrentLoadedExport.FileRef.getNameEntry(propName), export: CurrentLoadedExport);
            }
            return ArrayType.Int;
        }

        //private List<string> GetEnumValues(string enumName, int propName)
        private List<string> GetEnumValues(string enumName, string propName)
        {
            switch (CurrentLoadedExport.FileRef.Game)
            {
                case MEGame.ME1:
                    return ME1UnrealObjectInfo.getEnumfromProp(CurrentLoadedExport.ClassName, propName);
                case MEGame.ME2:
                    return ME2UnrealObjectInfo.getEnumfromProp(CurrentLoadedExport.ClassName, propName);
                case MEGame.ME3:
                case MEGame.UDK:
                    return ME3UnrealObjectInfo.getEnumValues(enumName, true);
            }
            return null;
        }
        #endregion

        private void Interpreter_ToggleHexboxWidth_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            GridLength len = HexboxColumnDefinition.Width;
            if (len.Value < HexboxColumnDefinition.MaxWidth)
            {
                HexboxColumnDefinition.Width = new GridLength(HexboxColumnDefinition.MaxWidth);
            }
            else
            {
                HexboxColumnDefinition.Width = new GridLength(HexboxColumnDefinition.MinWidth);
            }
        }

        private void Interpreter_TreeViewSelectedItemChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<object> e)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, (NoArgDelegate)delegate { UpdateHexboxPosition(e.NewValue as UPropertyTreeViewEntry); });
            UPropertyTreeViewEntry newSelectedItem = (UPropertyTreeViewEntry)e.NewValue;
            //list of visible elements for editing
            List<FrameworkElement> SupportedEditorSetElements = new List<FrameworkElement>();
            if (newSelectedItem != null && newSelectedItem.Property != null)
            {
                switch (newSelectedItem.Property)
                {
                    case IntProperty ip:
                        Value_TextBox.Text = ip.Value.ToString();
                        SupportedEditorSetElements.Add(Value_TextBox);
                        break;

                    case FloatProperty fp:
                        Value_TextBox.Text = fp.Value.ToString();
                        SupportedEditorSetElements.Add(Value_TextBox);
                        break;
                    case BoolProperty bp:
                        {
                            SupportedEditorSetElements.Add(Value_ComboBox);
                            List<string> values = new List<string>(new string[] { "True", "False" });
                            Value_ComboBox.ItemsSource = values;
                            Value_ComboBox.SelectedIndex = bp.Value ? 0 : 1; //true : false
                        }
                        break;
                    case ObjectProperty op:
                        Value_TextBox.Text = op.Value.ToString();
                        UpdateParsedEditorValue();
                        SupportedEditorSetElements.Add(Value_TextBox);
                        SupportedEditorSetElements.Add(ParsedValue_TextBlock);
                        break;
                    case NameProperty np:
                        Value_TextBox.Text = CurrentLoadedExport.FileRef.findName(np.Value.Name).ToString();
                        NameIndex_TextBox.Text = np.Value.Number.ToString();

                        UpdateParsedEditorValue();
                        SupportedEditorSetElements.Add(Value_TextBox);
                        SupportedEditorSetElements.Add(NameIndexPrefix_TextBlock);
                        SupportedEditorSetElements.Add(NameIndex_TextBox);
                        SupportedEditorSetElements.Add(ParsedValue_TextBlock);
                        break;
                    case EnumProperty ep:
                        {
                            SupportedEditorSetElements.Add(Value_ComboBox);
                            List<string> values = ep.EnumValues;
                            Value_ComboBox.ItemsSource = values;
                            int indexSelected = values.IndexOf(ep.Value.Name);
                            Value_ComboBox.SelectedIndex = indexSelected;
                        }
                        break;
                    case StringRefProperty strrefp:
                        {
                            Value_TextBox.Text = strrefp.Value.ToString();
                            SupportedEditorSetElements.Add(Value_TextBox);
                            SupportedEditorSetElements.Add(ParsedValue_TextBlock);
                            UpdateParsedEditorValue();
                        }
                        break;
                }

                Set_Button.Visibility = SupportedEditorSetElements.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

                if ((newSelectedItem.Property.GetType().IsOfGenericType(typeof(ArrayProperty<>)) || (newSelectedItem.Parent.Property != null && newSelectedItem.Parent.Property.GetType().IsOfGenericType(typeof(ArrayProperty<>)))))
                {
                    //The selected property is a child of an array property or is an array property
                    SupportedEditorSetElements.Add(EditorSet_ArraySetSeparator);
                    SupportedEditorSetElements.Add(AddArrayElement_Button);
                    if ((newSelectedItem.Parent.Property != null && newSelectedItem.Parent.Property.GetType().IsOfGenericType(typeof(ArrayProperty<>))))
                    {
                        //only allow remove on children
                        SupportedEditorSetElements.Add(RemoveArrayElement_Button);
                    }
                }

                //Hide the non-used controls
                foreach (FrameworkElement fe in EditorSetElements)
                {
                    fe.Visibility = SupportedEditorSetElements.Contains(fe) ? Visibility.Visible : Visibility.Collapsed;
                }
                EditorSet_Separator.Visibility = SupportedEditorSetElements.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            }
        }

        private void UpdateParsedEditorValue()
        {
            UPropertyTreeViewEntry tvi = (UPropertyTreeViewEntry)Interpreter_TreeView.SelectedItem;
            if (tvi != null && tvi.Property != null)

                switch (tvi.Property)
                {
                    case ObjectProperty op:
                        {
                            if (int.TryParse(Value_TextBox.Text, out int index))
                            {
                                if (index == 0)
                                {
                                    ParsedValue_TextBlock.Text = "Null";
                                }
                                else
                                {
                                    var entry = CurrentLoadedExport.FileRef.getEntry(index);
                                    if (entry != null)
                                    {
                                        ParsedValue_TextBlock.Text = entry.GetFullPath;
                                    }
                                    else
                                    {
                                        ParsedValue_TextBlock.Text = "Index out of bounds of entry list";
                                    }
                                }
                            }
                            else
                            {
                                ParsedValue_TextBlock.Text = "Invalid value";
                            }
                        }
                        break;
                    case StringRefProperty sp:
                        {
                            if (int.TryParse(Value_TextBox.Text, out int index))
                            {
                                if (CurrentLoadedExport.FileRef.Game == MEGame.ME3)
                                {
                                    string str = ME3TalkFiles.findDataById(index);
                                    str = str.Replace("\n", "[\\n]");
                                    if (str.Length > 80)
                                    {
                                        str = str.Substring(0, 80) + "...";
                                    }
                                    ParsedValue_TextBlock.Text = ME3TalkFiles.tlkList.Count == 0 ? "(.tlk not loaded)" : str.Replace(System.Environment.NewLine, "[\\n]");
                                }
                            }
                            else
                            {
                                ParsedValue_TextBlock.Text = "Invalid value";
                            }
                        }
                        break;
                    case NameProperty np:
                        {
                            if (int.TryParse(Value_TextBox.Text, out int index) && int.TryParse(NameIndex_TextBox.Text, out int number))
                            {
                                if (index >= 0 && index < CurrentLoadedExport.FileRef.Names.Count)
                                {
                                    ParsedValue_TextBlock.Text = CurrentLoadedExport.FileRef.getNameEntry(index) + "_" + number;
                                }
                                else
                                {
                                    ParsedValue_TextBlock.Text = "Name index out of range";
                                }
                            }
                            else
                            {
                                ParsedValue_TextBlock.Text = "Invalid value(s)";
                            }
                        }
                        break;
                }
        }
        /// <summary>
        /// Tree view selected item changed. This runs in a delegate due to how multithread bubble-up items work with treeview.
        /// Without this delegate, the item selected will randomly be a parent item instead.
        /// From https://www.codeproject.com/Tips/208896/WPF-TreeView-SelectedItemChanged-called-twice
        /// </summary>
        private delegate void NoArgDelegate();

        private void UpdateHexboxPosition(UPropertyTreeViewEntry newSelectedItem)
        {
            if (newSelectedItem != null && newSelectedItem.Property != null)
            {
                var hexPos = newSelectedItem.Property.ValueOffset;
                Interpreter_Hexbox.SelectionStart = hexPos;
                Interpreter_Hexbox.SelectionLength = 1; //maybe change
                Interpreter_Hexbox.UnhighlightAll();
                switch (newSelectedItem.Property)
                {
                    //case NoneProperty np:
                    //    Interpreter_Hexbox.Highlight(newSelectedItem.Property.ValueOffset - 4, 8);
                    //    return;
                    //case StructProperty sp:
                    //    break;
                    case ObjectProperty op:
                    case FloatProperty fp:
                    case IntProperty ip:
                        {
                            if (newSelectedItem.Parent.Property is StructProperty p && p.IsImmutable)
                            {
                                Interpreter_Hexbox.Highlight(newSelectedItem.Property.ValueOffset, 4);
                                return;
                            }
                            else if (newSelectedItem.Parent.Property is ArrayProperty<IntProperty> || newSelectedItem.Parent.Property is ArrayProperty<FloatProperty> || newSelectedItem.Parent.Property is ArrayProperty<ObjectProperty>)
                            {
                                Interpreter_Hexbox.Highlight(newSelectedItem.Property.ValueOffset, 4);
                                return;
                            }
                        }
                        //otherwise use the default
                        break;
                    case NameProperty np:
                        {
                            if (newSelectedItem.Parent.Property is StructProperty p && p.IsImmutable)
                            {
                                Interpreter_Hexbox.Highlight(newSelectedItem.Property.ValueOffset, 8);
                                return;
                            }
                            else if (newSelectedItem.Parent.Property is ArrayProperty<NameProperty>)
                            {
                                Interpreter_Hexbox.Highlight(newSelectedItem.Property.ValueOffset, 8);
                                return;
                            }
                        }
                        break;
                        //case EnumProperty ep:
                        //    Interpreter_Hexbox.Highlight(newSelectedItem.Property.ValueOffset - 32, newSelectedItem.Property.GetLength(CurrentLoadedExport.FileRef));
                        //    return;
                }

                if (CurrentLoadedExport.ClassName != "Class")
                {
                    Interpreter_Hexbox.Highlight(newSelectedItem.Property.StartOffset, newSelectedItem.Property.GetLength(CurrentLoadedExport.FileRef));
                }
                //else if (newSelectedItem.Parent.Property is ArrayProperty<IntProperty> || newSelectedItem.Parent.Property is ArrayProperty<FloatProperty> || newSelectedItem.Parent.Property is ArrayProperty<ObjectProperty>)
                //{
                //    Interpreter_Hexbox.Highlight(newSelectedItem.Property.ValueOffset, 4);
                //    return;
                //}
                //else if (newSelectedItem.Property is StructProperty)
                //{
                //    Interpreter_Hexbox.Highlight(newSelectedItem.Property.ValueOffset - 40, newSelectedItem.Property.GetLength(CurrentLoadedExport.FileRef));
                //    return;
                //}
                //else
                //{
                //}
                //array children
                /*if (newSelectedItem.Parent.Property != null && newSelectedItem.Parent.Property.PropType == PropertyType.ArrayProperty)
                {
                    if (newSelectedItem.Property is NameProperty np)
                    {
                        Interpreter_Hexbox.Highlight(newSelectedItem.Property.ValueOffset, 8);
                    }
                    else
                    {
                        Interpreter_Hexbox.Highlight(newSelectedItem.Property.ValueOffset, 4);
                    }
                    return;
                }

                else if (newSelectedItem.Parent.Property != null && newSelectedItem.Parent.Property.PropType == PropertyType.StructProperty)
                {
                    //Determine if it is immutable or not, somehow.
                }

                else if (newSelectedItem.Property is StructProperty sp)
                {
                    //struct size highlighting... not sure how to do this with this little info
                }
                else if (newSelectedItem.Property is NameProperty np)
                {
                    Interpreter_Hexbox.Highlight(newSelectedItem.Property.ValueOffset - 24, 32);
                }
                else if (newSelectedItem.Property is BoolProperty bp)
                {
                    Interpreter_Hexbox.Highlight(newSelectedItem.Property.ValueOffset - 24, 25);
                }
                else if (newSelectedItem.Parent.PropertyType != PropertyType.ArrayProperty.ToString())
                {
                    Interpreter_Hexbox.Highlight(newSelectedItem.Property.ValueOffset - 24, 28);
                }*/
            }
        }

        private void Interpreter_Loaded(object sender, System.Windows.RoutedEventArgs e)
        {
            Interpreter_Hexbox = (HexBox)Interpreter_Hexbox_Host.Child;
        }

        private void Interpreter_SaveHexChanged_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            IByteProvider provider = Interpreter_Hexbox.ByteProvider;
            if (provider != null)
            {
                MemoryStream m = new MemoryStream();
                for (int i = 0; i < provider.Length; i++)
                    m.WriteByte(provider.ReadByte(i));
                CurrentLoadedExport.Data = m.ToArray();
            }
        }

        private void RemovePropertyCommand_Executed(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
        {
            UPropertyTreeViewEntry tvi = (UPropertyTreeViewEntry)Interpreter_TreeView.SelectedItem;
            if (tvi != null && tvi.Property != null)
            {
                CurrentLoadedProperties.Remove(tvi.Property);
                CurrentLoadedExport.WriteProperties(CurrentLoadedProperties);
                StartScan(); //may not be required as export will rescan
            }
        }

        private void ArrayOrderByValueCommand_Executed(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
        {
            //outdated. Will replace later
            //TODO
            TreeViewItem tvi = (TreeViewItem)Interpreter_TreeView.SelectedItem;
            if (tvi != null)
            {
                UProperty tag = (UProperty)tvi.Tag;
                ArrayType at = GetArrayType(tag.Name, CurrentLoadedExport); //we may need to account for substructs
                bool sorted = false;
                switch (at)
                {
                    case ArrayType.Object:
                        {
                            ArrayProperty<ObjectProperty> list = (ArrayProperty<ObjectProperty>)tag;
                            List<ObjectProperty> sortedList = list.ToList();
                            sortedList.Sort();
                            list.Values.Clear();
                            list.Values.AddRange(sortedList);
                            sorted = true;
                        }
                        break;
                    case ArrayType.Int:
                        {
                            ArrayProperty<IntProperty> list = (ArrayProperty<IntProperty>)tag;
                            List<IntProperty> sortedList = list.ToList();
                            sortedList.Sort();
                            list.Values.Clear();
                            list.Values.AddRange(sortedList);
                            sorted = true;
                        }
                        break;
                    case ArrayType.Float:
                        {
                            ArrayProperty<FloatProperty> list = (ArrayProperty<FloatProperty>)tag;
                            List<FloatProperty> sortedList = list.ToList();
                            sortedList.Sort();
                            list.Values.Clear();
                            list.Values.AddRange(sortedList);
                            sorted = true;
                        }
                        break;
                }
                if (sorted)
                {
                    ItemsControl i = GetSelectedTreeViewItemParent(tvi);
                    //write at root node level
                    if (i.Tag is nodeType && (nodeType)i.Tag == nodeType.Root)
                    {
                        CurrentLoadedExport.WriteProperty(tag);
                        StartScan();
                    }

                    //have to figure out how to deal with structproperties or array of array propertie
                    /*if (tvi.Tag is StructProperty)
                    {
                        StructProperty sp = tvi.Tag as StructProperty;
                        sp.Properties.
                        CurrentLoadedExport.WriteProperty(tag);
                        StartScan();
                    }*/
                }
                //PropertyCollection props = CurrentLoadedExport.GetProperties();
                //props.Remove(tag);
                //CurrentLoadedExport.WriteProperties(props);
                //
            }
        }

        public ItemsControl GetSelectedTreeViewItemParent(TreeViewItem item)
        {
            DependencyObject parent = VisualTreeHelper.GetParent(item);
            while (!(parent is TreeViewItem || parent is TreeView))
            {
                parent = VisualTreeHelper.GetParent(parent);
            }

            return parent as ItemsControl;
        }

        public override bool CanParse(IExportEntry exportEntry)
        {
            return true;
        }

        private void SetValue_Click(object sender, RoutedEventArgs e)
        {
            //todo: set value
            bool updated = false;
            UPropertyTreeViewEntry tvi = (UPropertyTreeViewEntry)Interpreter_TreeView.SelectedItem;
            if (tvi != null && tvi.Property != null)
            {
                UProperty tag = tvi.Property;
                switch (tag)
                {
                    case IntProperty ip:
                        {
                            //scoped for variable re-use
                            if (int.TryParse(Value_TextBox.Text, out int i) && i != ip.Value)
                            {
                                ip.Value = i;
                                updated = true;
                            }
                        }
                        break;
                    case FloatProperty fp:
                        {
                            if (float.TryParse(Value_TextBox.Text, out float f) && f != fp.Value)
                            {
                                fp.Value = f;
                                updated = true;
                            }
                        }
                        break;
                    case BoolProperty bp:
                        if (bp.Value != (Value_ComboBox.SelectedIndex == 0))
                        {
                            bp.Value = Value_ComboBox.SelectedIndex == 0; //0 = true
                            updated = true;
                        }
                        break;
                    case StrProperty sp:
                        if (sp.Value != Value_TextBox.Text)
                        {
                            sp.Value = Value_TextBox.Text;
                            updated = true;
                        }
                        break;
                    case StringRefProperty srp:
                        {
                            if (int.TryParse(Value_TextBox.Text, out int s) && s != srp.Value)
                            {
                                srp.Value = s;
                                updated = true;
                            }
                        }
                        break;
                    case ObjectProperty op:
                        {
                            if (int.TryParse(Value_TextBox.Text, out int o) && o != op.Value)
                            {
                                //Todo: Check if value is in bounds. We should never set an out of bounds item here
                                op.Value = o;
                                updated = true;
                            }
                        }
                        break;
                    case EnumProperty ep:
                        if (ep.Value != (string)Value_ComboBox.SelectedItem)
                        {
                            ep.Value = (string)Value_ComboBox.SelectedItem; //0 = true
                            updated = true;
                        }
                        break;
                    case ByteProperty bytep:
                        if (byte.TryParse(Value_TextBox.Text, out byte b) && b != bytep.Value)
                        {
                            bytep.Value = b;
                            updated = true;
                        }
                        break;
                    case NameProperty namep:
                        //Todo: Check name values are in range
                        bool nametableindexok = int.TryParse(Value_TextBox.Text, out int nameTableIndex);
                        bool nameindexok = int.TryParse(NameIndex_TextBox.Text, out int nameIndex);

                        if (nametableindexok && nameindexok)
                        {
                            NameReference nameRef = new NameReference
                            {
                                Name = CurrentLoadedExport.FileRef.getNameEntry(nameTableIndex),
                                Number = nameIndex
                            };
                            namep.Value = nameRef;
                            updated = true;
                        }
                        break;
                }
                //PropertyCollection props = CurrentLoadedExport.GetProperties();
                //props.Remove(tag);

                if (updated)
                {
                    //will cause a refresh from packageeditorwpf
                    CurrentLoadedExport.WriteProperties(CurrentLoadedProperties);
                }
                //StartScan();
            }
        }

        private void ValueTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return)
            {
                SetValue_Click(null, null);
            }
        }

        private void Value_TextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateParsedEditorValue();
        }

        private void AddArrayElement_Button_Click(object sender, RoutedEventArgs e)
        {
            UPropertyTreeViewEntry tvi = (UPropertyTreeViewEntry)Interpreter_TreeView.SelectedItem;
            if (tvi != null)
            {
                UProperty propertyToAddItemTo = null;

                if (tvi.Property.GetType().IsOfGenericType(typeof(ArrayProperty<>)))
                {
                    propertyToAddItemTo = tvi.Property;
                }
                if (tvi.Parent.Property != null && tvi.Parent.Property.GetType().IsOfGenericType(typeof(ArrayProperty<>)))
                {
                    propertyToAddItemTo = tvi.Parent.Property;
                }

                switch (propertyToAddItemTo)
                {
                    case ArrayProperty<NameProperty> anp:
                        NameReference nameRef = new NameReference
                        {
                            Name = CurrentLoadedExport.FileRef.getNameEntry(0),
                            Number = 0
                        };
                        NameProperty np = new NameProperty() { Value = nameRef };
                        anp.Add(np);
                        break;
                    case ArrayProperty<ObjectProperty> aop:
                        ObjectProperty op = new ObjectProperty(0);
                        aop.Add(op);
                        break;
                    case ArrayProperty<IntProperty> aip:
                        IntProperty ip = new IntProperty(0);
                        aip.Add(ip);
                        break;
                    case ArrayProperty<FloatProperty> afp:
                        FloatProperty fp = new FloatProperty(0);
                        afp.Add(fp);
                        break;

                        //TODO: Figure out how to do these
                        /*
                        case ArrayProperty<EnumProperty> aep:
                            EnumProperty ep = new EnumProperty()
                            break;
                        case ArrayProperty<StructProperty> asp:
                            //TODO: Figure out how to do this
                            break;
                            case ArrayProperty<ArrayProperty> aap:
                            //uh...
                                break;
                            */
                }
                CurrentLoadedExport.WriteProperties(CurrentLoadedProperties);
            }
        }

        private void RemoveArrayElement_Button_Click(object sender, RoutedEventArgs e)
        {
            UPropertyTreeViewEntry tvi = (UPropertyTreeViewEntry)Interpreter_TreeView.SelectedItem;
            if (tvi != null)
            {
                if (tvi.Parent.Property != null && tvi.Parent.Property.GetType().IsOfGenericType(typeof(ArrayProperty<>)))
                {
                    bool removed = tvi.ChildrenProperties.Remove(tvi);
                    Debug.WriteLine("removed: " + removed);
                }
                CurrentLoadedExport.WriteProperties(CurrentLoadedProperties);
            }
        }
    }

    [DebuggerDisplay("UPropertyTreeViewEntry | {DisplayName}")]
    public class UPropertyTreeViewEntry : INotifyPropertyChanged
    {
        protected void OnPropertyChanged([CallerMemberName] string propName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        }
        public event PropertyChangedEventHandler PropertyChanged;
        private Brush _foregroundColor = Brushes.DarkSeaGreen;
        private bool isSelected;
        public bool IsSelected
        {
            get { return this.isSelected; }
            set
            {
                if (value != this.isSelected)
                {
                    isSelected = value;
                    OnPropertyChanged("IsSelected");
                }
            }
        }

        private bool isExpanded;
        public bool IsExpanded
        {
            get { return this.isExpanded; }
            set
            {
                if (value != this.isExpanded)
                {
                    this.isExpanded = value;
                    OnPropertyChanged("IsExpanded");
                }
            }
        }

        /// <summary>
        /// Used for inline editing. Return true to allow inline editing for property type
        /// </summary>
        public bool EditableType
        {
            get
            {
                if (Property == null) return false;

                switch (Property.PropType)
                {
                    //case Unreal.PropertyType.BoolProperty:
                    //case Unreal.PropertyType.ByteProperty:
                    case Unreal.PropertyType.FloatProperty:
                    case Unreal.PropertyType.IntProperty:
                    //case Unreal.PropertyType.NameProperty:
                    //case Unreal.PropertyType.StringRefProperty:
                    case Unreal.PropertyType.StrProperty:
                        //case Unreal.PropertyType.ObjectProperty:
                        return true;
                }
                return false;
            }
        }

        public void ExpandParents()
        {
            if (Parent != null)
            {
                Parent.ExpandParents();
                Parent.IsExpanded = true;
            }
        }

        /// <summary>
        /// Flattens the tree into depth first order. Use this method for searching the list.
        /// </summary>
        /// <returns></returns>
        public List<UPropertyTreeViewEntry> FlattenTree()
        {
            List<UPropertyTreeViewEntry> nodes = new List<UPropertyTreeViewEntry>();
            nodes.Add(this);
            foreach (UPropertyTreeViewEntry tve in ChildrenProperties)
            {
                nodes.AddRange(tve.FlattenTree());
            }
            return nodes;
        }

        public event EventHandler PropertyUpdated;

        public UPropertyTreeViewEntry Parent { get; set; }

        /// <summary>
        /// The UProperty object from the export's properties that this node represents
        /// </summary>
        public UProperty Property { get; set; }
        /// <summary>
        /// List of children properties that link to this node.
        /// Only Struct and ArrayProperties will have this populated.
        /// </summary>
        public ObservableCollectionExtended<UPropertyTreeViewEntry> ChildrenProperties { get; set; }
        public UPropertyTreeViewEntry(UProperty property, string displayName = null)
        {
            Property = property;
            DisplayName = displayName;
            ChildrenProperties = new ObservableCollectionExtended<UPropertyTreeViewEntry>();
        }

        public UPropertyTreeViewEntry()
        {
            ChildrenProperties = new ObservableCollectionExtended<UPropertyTreeViewEntry>();
        }

        private string _editableValue;
        public string EditableValue
        {
            get
            {
                if (_editableValue != null) return _editableValue;
                return "";
            }
            set
            {
                //Todo: Write property value here
                if (_editableValue != null && _editableValue != value)
                {
                    switch (Property.PropType)
                    {
                        case Unreal.PropertyType.IntProperty:
                            if (int.TryParse(value, out int parsedIntVal))
                            {
                                (Property as IntProperty).Value = parsedIntVal;
                                PropertyUpdated?.Invoke(this, EventArgs.Empty);
                                HasChanges = true;
                            }
                            else
                            {
                                return;
                            }
                            break;
                        case Unreal.PropertyType.FloatProperty:
                            if (float.TryParse(value, out float parsedFloatVal))
                            {
                                (Property as FloatProperty).Value = parsedFloatVal;
                                PropertyUpdated?.Invoke(this, EventArgs.Empty);
                                HasChanges = true;
                            }
                            else
                            {
                                return;
                            }
                            break;
                    }
                }
                _editableValue = value;
            }
        }

        private bool _hasChanges;
        public bool HasChanges
        {
            get { return _hasChanges; }
            set
            {
                _hasChanges = value;
                OnPropertyChanged();
            }
        }

        private string _displayName;
        public string DisplayName
        {
            get
            {
                if (_displayName != null) return _displayName;
                return "DisplayName for this UPropertyTreeViewItem is null!";
                //string type = UIndex < 0 ? "Imp" : "Exp";
                //return $"({type}) {UIndex} {Entry.ObjectName}({Entry.ClassName})"; */
            }
            set { _displayName = value; }
        }

        public bool IsUpdatable = false; //set to

        private string _parsedValue;
        public string ParsedValue
        {
            get
            {
                if (_parsedValue != null) return _parsedValue;
                return "";
            }
            set
            {
                _parsedValue = value;

            }
        }

        public string PropertyType
        {
            get
            {
                if (Property != null)
                {
                    if (Property.PropType == Unreal.PropertyType.ArrayProperty)
                    {
                        //we don't have reference to current pcc so we cannot look this up at this time.
                        //return $"ArrayProperty({(Property as ArrayProperty).arrayType})";
                        var props = (Property as ArrayPropertyBase).ValuesAsProperties;
                        return $"ArrayProperty - {props.Count()} item{(props.Count() != 1 ? "s" : "")}";

                    }
                    else if (Property.PropType == Unreal.PropertyType.StructProperty)
                    {
                        return $"StructProperty({(Property as StructProperty).StructType})";
                    }
                    else if (Property.PropType == Unreal.PropertyType.ByteProperty && Property is EnumProperty)
                    {
                        return "ByteProperty(Enum)"; //proptype and type don't seem to always match for some reason
                    }
                    else
                    {
                        return Property.PropType.ToString();
                    }
                }

                return "Currently loaded export";
                //string type = UIndex < 0 ? "Imp" : "Exp";
                //return $"({type}) {UIndex} {Entry.ObjectName}({Entry.ClassName})"; */
            }
            //set { _displayName = value; }
        }

        public override string ToString()
        {
            return "UPropertyTreeViewEntry " + DisplayName;
        }
    }

}
