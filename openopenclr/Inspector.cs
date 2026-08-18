using System;
using System.Collections.Generic;
using System.Globalization;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json;  // 添加这个 - 用于 JsonConvert 和 Formatting
using openopenclr.NativeEvents;

namespace openopenclr
{
    public partial class Inspector : Form
    {
        private class TypeTreeEntry
        {
            internal Dictionary<string, TypeTreeEntry> directories;
            internal HashSet<string> files;
            internal int nestingLevel;

            internal void AddFile(string name)
            {
                if (files == null)
                    files = new HashSet<string>();

                files.Add(name);
            }

            internal TypeTreeEntry GetOrAddDirectory(string name)
            {
                if (directories == null)
                    directories = new Dictionary<string, TypeTreeEntry>();

                if (!directories.TryGetValue(name, out TypeTreeEntry subdir))
                {
                    subdir = new TypeTreeEntry(nestingLevel + 1);
                    directories.Add(name, subdir);
                }

                return subdir;
            }

            internal TypeTreeEntry(int nestingLevel)
            {
                this.nestingLevel = nestingLevel;

                if (nestingLevel <= 2)
                {
                    directories = new Dictionary<string, TypeTreeEntry>();
                    files = new HashSet<string>();
                }
            }
        }

        internal EventWaitHandle IsFormReady { get; } = new EventWaitHandle(false, EventResetMode.ManualReset);

        private readonly Dictionary<string, TreeNode> allFileNodes = new Dictionary<string, TreeNode>();
        private readonly List<LinkLabel> extraInheritanceControls = new List<LinkLabel>();
        private int totalTypes = 0;

        private PropertyText currentPropertyText;
        private Dictionary<uint, byte[]> availablePropertyTexts = new Dictionary<uint, byte[]>();
        private uint requestedPropertyFlags = 0x20800003; // relative, own text, with indentation
        // 添加导出相关的字段
        private List<string> currentTypeList = new List<string>();
        private Button exportButton;
        // 添加字段
        private Dictionary<string, (PropertyText data, List<string> inheritanceChain)> typeDataCache = 
            new Dictionary<string, (PropertyText, List<string>)>();
        private HashSet<string> processedTypes = new HashSet<string>(); // 已处理的类型
        private string progressFilePath; // 进度文件路径
        private string exportRootPath; // 导出根目录

        public Inspector()
        {
            InitializeComponent();
            IsFormReady.Set();

            // 设置进度文件路径
            exportRootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Warframemetadata");
            progressFilePath = Path.Combine(exportRootPath, "fetch_progress.json");

            // 加载已有进度
            LoadProgress();

            // 添加导出按钮
            exportButton = new Button
            {
                Text = "Export",
                Location = new Point(button1.Left - 85, button1.Top),
                Size = new Size(75, button1.Height),
                Enabled = true
            };
            exportButton.Click += ExportButton_Click;
            this.Controls.Add(exportButton);
        }

        // 加载进度
        private void LoadProgress()
        {
            try
            {
                if (File.Exists(progressFilePath))
                {
                    var progress = JsonConvert.DeserializeObject<HashSet<string>>(File.ReadAllText(progressFilePath));
                    if (progress != null)
                    {
                        processedTypes = progress;
                        NativeInterface.LogToConsole($"Loaded progress: {processedTypes.Count} types already processed");
                    }
                }
            }
            catch (Exception ex)
            {
                NativeInterface.LogToConsole($"Failed to load progress: {ex.Message}");
            }
        }

        // 保存进度
        private void SaveProgress()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(progressFilePath));
                File.WriteAllText(progressFilePath, JsonConvert.SerializeObject(processedTypes));
            }
            catch (Exception ex)
            {
                NativeInterface.LogToConsole($"Failed to save progress: {ex.Message}");
            }
        }

        // 导出按钮点击事件
        // 修改导出按钮点击事件
        private void ExportButton_Click(object sender, EventArgs e)
        {
            if (currentTypeList == null || currentTypeList.Count == 0)
                return;
            
            try
            {
                int unprocessedCount = currentTypeList.Count - processedTypes.Count;
                
                var result = MessageBox.Show(
                    $"Total types: {currentTypeList.Count}\n" +
                    $"Already processed: {processedTypes.Count}\n" +
                    $"Remaining: {unprocessedCount}\n\n" +
                    "Do you want to continue fetching data?\n" +
                    "Choose No to stop and use existing data.",
                    "Export Status", 
                    MessageBoxButtons.YesNoCancel, 
                    MessageBoxIcon.Question);
                
                if (result == DialogResult.Cancel)
                    return;
                
                if (result == DialogResult.Yes && unprocessedCount > 0)
                {
                    exportButton.Enabled = false;
                    exportButton.Text = "Fetching...";
                    StartFetchingAllTypeData();
                    return;
                }
                
                MessageBox.Show($"Data saved to: {exportRootPath}\nProcessed: {processedTypes.Count} types", 
                    "Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}", 
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // 导出类型到文件系统
        private void ExportTypesToFileSystem(List<string> allTypes, string rootPath)
        {
            NativeInterface.LogToConsole($"=== ExportTypesToFileSystem called ===");
            NativeInterface.LogToConsole($"Total types to export: {allTypes.Count}");
            
            if (allTypes.Count == 0)
            {
                NativeInterface.LogToConsole("No types to export");
                return;
            }

            // 过滤出测试路径下的类型
            // var filteredTypes = allTypes.Where(t => 
            //     t.StartsWith("/Lotus/Syndicates", StringComparison.OrdinalIgnoreCase)
            // ).ToList();
            
            // NativeInterface.LogToConsole($"Filtered types: {filteredTypes.Count}");
            
            // if (filteredTypes.Count == 0)
            // {
            //     NativeInterface.LogToConsole("No types found");
            //     return;
            // }
            
            // 使用与 PopulateTreeView 相同的逻辑构建树
            TypeTreeEntry rootEntry = new TypeTreeEntry(0);
            
            foreach (var type in allTypes)
            {
                if (type.Length == 0 || type[0] != '/')
                    continue;
                
                TypeTreeEntry currEntry = rootEntry;
                int lastComponentStartIdx = 1;
                for (; ;)
                {
                    int slashIdx = type.IndexOf('/', lastComponentStartIdx);
                    if (slashIdx == -1) // file
                    {
                        currEntry.AddFile(type.Substring(lastComponentStartIdx));
                        break;
                    }
                    else // directory
                    {
                        currEntry = currEntry.GetOrAddDirectory(type.Substring(lastComponentStartIdx, slashIdx - lastComponentStartIdx));
                        lastComponentStartIdx = slashIdx + 1;
                    }
                }
            }
            
            // 创建根目录
            Directory.CreateDirectory(rootPath);
            
            // 递归创建目录和文件，传入空相对路径
            CreateDirectoriesAndFiles(rootEntry, rootPath, "");
        }

        // 核心导出函数 - 复用 PopulateTreeView 的逻辑
        // 修改导出函数，包含 data 字段
        private void CreateDirectoriesAndFiles(TypeTreeEntry entry, string currentPath, string relativePath = "")
        {
            // 创建文件
            if (entry.files != null)
            {
                foreach (var file in entry.files)
                {
                    string filePath = Path.Combine(currentPath, file + ".json");
                    
                    var typeInfo = new Dictionary<string, object>
                    {
                        ["typeName"] = file
                    };
                    
                    // 从缓存获取数据和继承链
                    lock (typeDataCache)
                    {
                        // 直接通过文件名查找（避免路径构建问题）
                        PropertyText propertyText = null;
                        List<string> inheritanceChain = null;
                        string fullPath = null;

                        // 查找匹配的缓存项
                        foreach (var kvp in typeDataCache)
                        {
                            if (kvp.Key.EndsWith("/" + file))
                            {
                                fullPath = kvp.Key;
                                var cachedData = kvp.Value;
                                propertyText = cachedData.data;
                                inheritanceChain = cachedData.inheritanceChain;
                                break;

                                // var (propertyText, inheritanceChain) = kvp.Value;
                                
                                // typeInfo["fullPath"] = kvp.Key;
                                
                                // // 添加 parent 字段（继承链的第二个元素，如果存在）
                                // if (inheritanceChain != null && inheritanceChain.Count > 1)
                                // {
                                //     typeInfo["parent"] = inheritanceChain[1];
                                //     NativeInterface.LogToConsole($"Parent for {file}: {inheritanceChain[1]}");
                                // }
                                // else if (inheritanceChain != null && inheritanceChain.Count > 0)
                                // {
                                //     typeInfo["parent"] = inheritanceChain[0];
                                //     NativeInterface.LogToConsole($"No parent found, using self: {inheritanceChain[0]}");
                                // }
                                
                                // // 添加完整的继承链（可选）
                                // if (inheritanceChain != null && inheritanceChain.Count > 0)
                                // {
                                //     typeInfo["inheritanceChain"] = inheritanceChain;
                                // }
                                
                                // // 添加数据
                                // if (propertyText != null)
                                // {
                                //     typeInfo["data"] = propertyText.PropertyData;
                                // }
                                
                                // break;
                            }
                        }

                        if (fullPath != null)
                        {
                            typeInfo["fullPath"] = fullPath;
                            
                            // 添加 parent 字段
                            if (inheritanceChain != null && inheritanceChain.Count > 1)
                            {
                                typeInfo["parent"] = inheritanceChain[1];
                            }
                            
                            // 添加完整的继承链
                            // if (inheritanceChain != null && inheritanceChain.Count > 0)
                            // {
                            //     typeInfo["inheritanceChain"] = inheritanceChain;
                            // }
                            
                            // 添加数据
                            if (propertyText != null)
                            {
                                var cleanedData = CleanPropertyDataForExport(propertyText.PropertyData);
                                typeInfo["data"] = cleanedData;
                                // typeInfo["data"] = propertyText.PropertyData;
                                // typeInfo["textData"] = propertyText.TextData;
                                // typeInfo["jsonData"] = propertyText.JsonData;
                            }
                        }
                        else
                        {
                            typeInfo["fullPath"] = "/" + file; // 如果找不到，使用文件名
                        }
                    }
                    
                    File.WriteAllText(filePath, JsonConvert.SerializeObject(typeInfo, Formatting.Indented));
                }
            }
            
            // 创建子目录
            if (entry.directories != null)
            {
                foreach (var dir in entry.directories)
                {
                    string dirPath = Path.Combine(currentPath, dir.Key);
                    Directory.CreateDirectory(dirPath);
                    CreateDirectoriesAndFiles(dir.Value, dirPath);
                }
            }
        }

        // 添加批量获取数据的方法（在导出前调用）
        private void FetchAllTypeData()
        {
            if (currentTypeList == null || currentTypeList.Count == 0)
                return;
            
            NativeInterface.LogToConsole($"Starting to fetch data for {currentTypeList.Count} types");
            
            int fetchedCount = 0;
            foreach (var typePath in currentTypeList)
            {
                if (!typeDataCache.ContainsKey(typePath))
                {
                    // 请求类型信息
                    NativeInterface.RequestTypeInfo(typePath);
                    
                    // 等待响应处理
                    
                    while (!typeDataCache.ContainsKey(typePath))
                    {
                        Application.DoEvents();
                    }
                    
                    if (typeDataCache.ContainsKey(typePath))
                    {
                        fetchedCount++;
                        if (fetchedCount % 10 == 0)
                        {
                            NativeInterface.LogToConsole($"Fetched {fetchedCount} types");
                        }
                    }
                }
            }
            
            NativeInterface.LogToConsole($"Completed fetching data. Total cached: {typeDataCache.Count}");
        }

        // 添加一个字段用于测试
        //private string testFilter = "/Lotus/Syndicate";

        // 开始批量获取数据
        private void StartFetchingAllTypeData()
        {
            typesToFetch = new List<string>();
            
            // 只获取未处理的类型
            foreach (var typePath in currentTypeList)
            {
                lock (processedTypes)
                {
                    if (!processedTypes.Contains(typePath))
                    {
                        typesToFetch.Add(typePath);
                    }
                }
            }
            
            if (typesToFetch.Count == 0)
            {
                MessageBox.Show("All types have been processed.", 
                    "Nothing to Fetch", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            
            currentFetchIndex = 0;
            isFetchingData = true;
            
            NativeInterface.LogToConsole($"Starting to fetch {typesToFetch.Count} types (skipping {processedTypes.Count} already processed)");
            
            // 开始获取第一个类型
            FetchNextType();
        }

        // 添加深度复制和清理方法
        private Dictionary<string, object> CleanPropertyDataForExport(Dictionary<string, object> originalData)
        {
            var cleanedData = new Dictionary<string, object>();
            
            foreach (var kvp in originalData)
            {
                cleanedData[kvp.Key] = CleanValueForExport(kvp.Value);
            }
            
            return cleanedData;
        }

        private object CleanValueForExport(object value)
        {
            if (value is string strValue)
            {
                return CleanStringForExport(strValue);
            }
            else if (value is Dictionary<string, object> dictValue)
            {
                return CleanPropertyDataForExport(dictValue);
            }
            else if (value is List<object> listValue)
            {
                var cleanedList = new List<object>();
                foreach (var item in listValue)
                {
                    cleanedList.Add(CleanValueForExport(item));
                }
                return cleanedList;
            }
            
            return value;
        }

        private object CleanStringForExport(string str)
        {
            // 去除可能的空白
            string trimmed = str.Trim();
            
            // 1. 检查是否包含引号（明确的字符串）
            if (trimmed.Length >= 2 && trimmed.StartsWith("\"") && trimmed.EndsWith("\""))
            {
                // 去除引号
                return trimmed.Substring(1, trimmed.Length - 2);
            }
            
            // 2. 检查是否是布尔值
            if (trimmed.Equals("true", StringComparison.OrdinalIgnoreCase))
                return true;
            if (trimmed.Equals("false", StringComparison.OrdinalIgnoreCase))
                return false;
            
            // 3. 检查是否是整数
            if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out long longValue))
            {
                if (longValue >= int.MinValue && longValue <= int.MaxValue)
                    return (int)longValue;
                return longValue;
            }
            
            // 4. 检查是否是浮点数
            if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double doubleValue))
            {
                if (double.IsNaN(doubleValue) || double.IsInfinity(doubleValue))
                    return trimmed;
                
                // 尝试转换为 float
                if (float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out float floatValue))
                {
                    if ((double)floatValue == doubleValue)
                        return floatValue;
                }
                
                return doubleValue;
            }
            
            // 5. 默认返回字符串（去除引号如果有的话）
            return trimmed;
        }

        private void OnInheritanceLinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            LinkLabel label = (LinkLabel)sender;
            if (!allFileNodes.TryGetValue(label.Text, out TreeNode node))
            {
                MessageBox.Show("Could not navigate to the selected type (not found)", "OpenWF Enabler", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            treeView1.SelectedNode = node;
            treeView1.Focus();
        }

        internal void SetRefreshing(bool isRefreshing)
        {
            treeView1.Enabled = !isRefreshing;
            button1.Enabled = !isRefreshing;
            button1.Text = isRefreshing ? "Refreshing..." : "Refresh list";
            label1.Text = isRefreshing ? "Refreshing, please wait..." : $"{totalTypes} types found";
        }

        internal void SetPropertyText(uint flags)
        {
            if (availablePropertyTexts == null || !availablePropertyTexts.TryGetValue(flags, out byte[] propText))
            {
                currentPropertyText = null;
                textBox1.ForeColor = Color.DarkRed;
                textBox1.Text = "No property text is currently available.";
            }
            else
            {
                try
                {
                    currentPropertyText = new PropertyText(propText);
                }
                catch (Exception ex)
                {
                    currentPropertyText = null;
                    textBox1.ForeColor = Color.DarkRed;
                    textBox1.Text = $"Property text is invalid:\r\n{ex}";
                    return;
                }

                textBox1.ForeColor = Color.Black;
                textBox1.Text = radioButton1.Checked ? currentPropertyText.TextData : currentPropertyText.JsonData;
            }
        }

        internal void SetAvailablePropertyTexts(Dictionary<uint, byte[]> availableTexts)
        {
            availablePropertyTexts = availableTexts;
            SetPropertyText(requestedPropertyFlags);
        }

        internal void SetInheritanceInfo(List<string> inheritanceChain)
        {
            if (inheritanceChain == null || inheritanceChain.Count == 0)
            {
                foreach (var linkLabel in extraInheritanceControls)
                {
                    linkLabel.Dispose();
                    tabPage1.Controls.Remove(linkLabel);
                }

                extraInheritanceControls.Clear();

                label3.Visible = false;
                label4.Visible = false;
                linkLabel2.Visible = false;
            }
            else
            {
                inheritanceChain = inheritanceChain.Reverse<string>().ToList(); // make a copy
                linkLabel2.Text = inheritanceChain[0];

                LinkLabel prevLinkLabel = linkLabel2;
                foreach (var type in inheritanceChain.Skip(1))
                {
                    LinkLabel ll = new LinkLabel
                    {
                        AutoSize = true,
                        Visible = true,
                        TabIndex = prevLinkLabel.TabIndex + 1,
                        TabStop = true,
                        Text = type,
                        Location = new Point(prevLinkLabel.Location.X + 8, prevLinkLabel.Location.Y + 16)
                    };

                    tabPage1.Controls.Add(ll);
                    extraInheritanceControls.Add(ll);

                    ll.LinkClicked += OnInheritanceLinkClicked;

                    prevLinkLabel = ll;
                }

                label3.Visible = true;
                label4.Visible = true;
                linkLabel2.Visible = true;
            }

            tabPage1.Refresh();
        }

        internal void PopulateTreeView(List<string> allTypes)
        {
            totalTypes = 0;

            TypeTreeEntry rootEntry = new TypeTreeEntry(0);

            allTypes.Sort();
            foreach (var type in allTypes)
            {
                if (type.Length == 0 || type[0] != '/')
                {
                    NativeInterface.LogToConsole($"Ignored abnormal type: {type}");
                    continue;
                }

                TypeTreeEntry currEntry = rootEntry;
                int lastComponentStartIdx = 1;
                for (; ;)
                {
                    int slashIdx = type.IndexOf('/', lastComponentStartIdx);
                    if (slashIdx == -1) // file
                    {
                        currEntry.AddFile(type.Substring(lastComponentStartIdx));
                        break;
                    }
                    else // directory
                    {
                        currEntry = currEntry.GetOrAddDirectory(type.Substring(lastComponentStartIdx, slashIdx - lastComponentStartIdx));
                        lastComponentStartIdx = slashIdx + 1;
                    }
                }
            }

            void InsertEntries(TreeNodeCollection parentEntry, string parentName, string name, TypeTreeEntry children)
            {
                string fullPath = parentName + name;
                TreeNode newEntry = parentEntry.Add(fullPath, name);

                if (children != null)
                {
                    if (children.directories != null)
                    {
                        foreach (var subdir in children.directories)
                        {
                            InsertEntries(newEntry.Nodes, fullPath, subdir.Key + "/", subdir.Value);
                        }
                    }

                    if (children.files != null)
                    {
                        foreach (var file in children.files)
                        {
                            ++totalTypes;

                            string fullName = fullPath + file;
                            allFileNodes[fullName] = newEntry.Nodes.Add(fullName, file);
                        }
                    }
                }
            }

            InsertEntries(treeView1.Nodes, "", "/", rootEntry);
        }

        // 添加字段来存储当前的请求范围
        private bool currentRequestAllTypes = false; // 默认与 checkBox1 的状态一致

        internal void OnTypeListReceived(ResponseTypeListEvent evt)
        {
            currentTypeList = evt.AllTypes; // 直接使用，不要复制

            // 记录当前的请求范围（根据 checkBox1 的状态）
            currentRequestAllTypes = !checkBox1.Checked;

            NativeInterface.LogToConsole($"Received {currentTypeList.Count} types (requestAllTypes={currentRequestAllTypes})");

            BeginInvoke((Action)(() =>
            {
                treeView1.BeginUpdate();
                try
                {
                    // suppressing WM_NOTIFY improves treeview clearing performance because:
                    // - each element is deleted separately (internally, in the native comctl32.dll)
                    // - each element's deletion triggers WM_NOTIFY that's sent using a syscall
                    // - so we just prevent the syscall from being sent which improves performance by like x100
                    // - we don't care about WM_NOTIFY anyway
                    NativeInterface.SuppressTreeNodeEvents(true);
                    treeView1.Nodes.Clear();
                    allFileNodes.Clear();
                    NativeInterface.SuppressTreeNodeEvents(false);

                    PopulateTreeView(evt.AllTypes);
                    if (treeView1.Nodes.Count > 0)
                        treeView1.Nodes[0].Expand();

                    exportButton.Enabled = true; // 启用导出按钮
                }
                finally
                {
                    treeView1.EndUpdate();
                    SetRefreshing(false);
                }
            }));
        }

        private bool isFetchingData = false;
        private int currentFetchIndex = 0;
        private List<string> typesToFetch = new List<string>();

        internal void OnTypeInfoReceived(ResponseTypeInfoEvent evt)
        {
            try
            {
                NativeInterface.LogToConsole($"=== OnTypeInfoReceived called ===");
                
                // 先在线程外处理数据解析
                PropertyText parsedData = null;
                string typePath = null;
                List<string> inheritanceChain = null;
                
                if (!evt.IsError && evt.InheritanceChain != null && evt.InheritanceChain.Count > 0)
                {
                    typePath = evt.InheritanceChain[0];
                    inheritanceChain = evt.InheritanceChain;
                    
                    NativeInterface.LogToConsole($"Type path: {typePath}");
                    NativeInterface.LogToConsole($"Inheritance chain count: {inheritanceChain.Count}");

                    if (evt.PropertyTexts != null && evt.PropertyTexts.Count > 0)
                    {
                        foreach (var kvp in evt.PropertyTexts)
                        {
                            try
                            {
                                parsedData = new PropertyText(kvp.Value);
                                break;
                            }
                            catch (Exception ex)
                            {
                                NativeInterface.LogToConsole($"Failed to parse property text: {ex.Message}");
                            }
                        }
                        
                        if (typePath != null && parsedData != null)
                        {
                            // 立即保存到文件，而不是缓存
                            SaveTypeToFile(typePath, parsedData, inheritanceChain);
                            
                            // 标记为已处理
                            lock (processedTypes)
                            {
                                processedTypes.Add(typePath);
                            }
                            
                            // 每处理10个类型保存一次进度
                            if (processedTypes.Count % 10 == 0)
                            {
                                SaveProgress();
                            }
                        }
                    }
                }
                
                // 继续获取下一个类型
                if (isFetchingData)
                {
                    FetchNextType();
                }
                
                // 在 UI 线程更新界面
                BeginInvoke((Action)(() =>
                {
                    label2.ResetText();
                    SetAvailablePropertyTexts(null);
                    SetInheritanceInfo(null);

                    if (evt.IsError)
                    {
                        label2.Text = evt.ErrorMessage;
                    }
                    else
                    {
                        label2.Text = $"Type info for {evt.InheritanceChain[0]}";
                        SetAvailablePropertyTexts(evt.PropertyTexts);
                        SetInheritanceInfo(evt.InheritanceChain);
                    }
                }));
            }
            catch (Exception ex)
            {
                NativeInterface.LogToConsole($"Error in OnTypeInfoReceived: {ex.Message}");
            }
        }

        // 直接保存类型到文件
        private void SaveTypeToFile(string typePath, PropertyText propertyText, List<string> inheritanceChain)
        {
            try
            {
                // 构建文件路径
                string cleanPath = typePath.TrimStart('/');
                string[] parts = cleanPath.Split('/');
                
                if (parts.Length == 0)
                    return;
                
                string currentDir = exportRootPath;
                
                // 创建目录（除了最后一个组件）
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    currentDir = Path.Combine(currentDir, parts[i]);
                }
                Directory.CreateDirectory(currentDir);
                
                // 构建文件路径
                string fileName = parts[parts.Length - 1] + ".json";
                string filePath = Path.Combine(currentDir, fileName);
                
                // 构建 JSON 对象
                var typeInfo = new Dictionary<string, object>
                {
                    // ["typeName"] = parts[parts.Length - 1],
                    // ["fullPath"] = typePath
                };
                
                // 添加 parent 字段
                if (inheritanceChain != null && inheritanceChain.Count > 1)
                {
                    typeInfo["parent"] = inheritanceChain[1];
                }
                
                // 添加继承链
                // if (inheritanceChain != null && inheritanceChain.Count > 0)
                // {
                //     typeInfo["inheritanceChain"] = inheritanceChain;
                // }
                
                // 添加数据
                var cleanedData = CleanPropertyDataForExport(propertyText.PropertyData);
                typeInfo["data"] = cleanedData;
                // typeInfo["data"] = propertyText.PropertyData;
                // typeInfo["textData"] = propertyText.TextData;
                // typeInfo["jsonData"] = propertyText.JsonData;
                
                // 写入文件
                File.WriteAllText(filePath, JsonConvert.SerializeObject(typeInfo, Formatting.Indented));
                
                NativeInterface.LogToConsole($"Saved: {filePath}");
            }
            catch (Exception ex)
            {
                NativeInterface.LogToConsole($"Failed to save {typePath}: {ex.Message}");
            }
        }

        // 异步获取下一个类型
        private void FetchNextType()
        {
            try
            {
                if (currentFetchIndex < typesToFetch.Count)
                {
                    string typePath = typesToFetch[currentFetchIndex];
                    currentFetchIndex++;
                    
                    NativeInterface.LogToConsole($"Fetching {currentFetchIndex}/{typesToFetch.Count}: {typePath}");
                    NativeInterface.RequestTypeInfo(typePath);
                    
                    // 每50个更新一次UI
                    if (currentFetchIndex % 50 == 0 || currentFetchIndex == typesToFetch.Count)
                    {
                        BeginInvoke((Action)(() =>
                        {
                            exportButton.Text = $"Fetching {currentFetchIndex}/{typesToFetch.Count}";
                            label1.Text = $"Processed: {processedTypes.Count}/{currentTypeList.Count}";
                        }));
                    }
                }
                else
                {
                    // 获取完成
                    isFetchingData = false;
                    SaveProgress(); // 最后保存一次进度
                    
                    NativeInterface.LogToConsole($"Fetch completed. Total processed: {processedTypes.Count}");
                    
                    BeginInvoke((Action)(() =>
                    {
                        exportButton.Enabled = true;
                        exportButton.Text = "Export";
                        MessageBox.Show($"Data fetching completed.\nProcessed: {processedTypes.Count} types\nSaved to: {exportRootPath}", 
                            "Fetch Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }));
                }
            }
            catch (Exception ex)
            {
                NativeInterface.LogToConsole($"Error in FetchNextType: {ex.Message}");
                isFetchingData = false;
                SaveProgress(); // 出错时也保存进度
                
                BeginInvoke((Action)(() =>
                {
                    exportButton.Enabled = true;
                    exportButton.Text = "Export";
                    MessageBox.Show($"Fetch error: {ex.Message}\nProgress saved. You can resume later.", 
                        "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }));
            }
        }

        private void button1_Click(object sender, EventArgs e)
        {
            currentRequestAllTypes = !checkBox1.Checked;
            NativeInterface.RequestTypeList(!checkBox1.Checked);
            SetRefreshing(true);
        }

        private void treeView1_AfterSelect(object sender, TreeViewEventArgs e)
        {
            if (!e.Node.Name.EndsWith("/"))
                NativeInterface.RequestTypeInfo(e.Node.Name);
        }

        private void linkLabel1_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            MessageBox.Show(this, "Checking this option will hide most irrelevant types that do not use property text. " +
                "It is recommended to ALWAYS keep it checked unless you're certain that the type you're looking for is behind this option.\n\n" +
                "Toggling this option requires a refresh.",
                "OpenWF Enabler", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void Inspector_Shown(object sender, EventArgs e)
        {
            BeginInvoke((Action)(() =>
            {
                button1.PerformClick(); // simulate type list refresh
            }));
        }

        private void Inspector_FormClosed(object sender, FormClosedEventArgs e)
        {
            NativeInterface.SuppressTreeNodeEvents(true); // improves performance of treeview clearing upon form close
        }

        private void linkLabel3_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            using (var settings = new PropertySettings(requestedPropertyFlags))
            {
                if (settings.ShowDialog(this) == DialogResult.OK)
                {
                    requestedPropertyFlags = settings.PropertyTextFlags;
                    SetPropertyText(requestedPropertyFlags);
                }
            }
        }

        private void radioButton1_CheckedChanged(object sender, EventArgs e)
        {
            SetPropertyText(requestedPropertyFlags);
        }

        private void radioButton2_CheckedChanged(object sender, EventArgs e)
        {
            SetPropertyText(requestedPropertyFlags);
        }
    }
}
