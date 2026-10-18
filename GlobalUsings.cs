// 全项目共享的 using 声明（C# global using）。
//
// 本项目原先在 60+ 个文件里各自重复同一批 using，partial class 的每个分片文件顶部
// 都贴一份 11 行的 "using 墙"，改一个文件要滚动半屏才看到代码。
// 这里把「无歧义、全项目普遍使用」的命名空间集中一次；有类型冲突风险的
// （System.Drawing / System.Windows.Forms / System.Windows.Media 等）仍留在各文件内显式导入。

global using System;
global using System.Collections.Generic;
global using System.Diagnostics;
global using System.IO;
global using System.Linq;
global using System.Reflection;
global using System.Text;
global using System.Text.Json;
global using System.Threading;
global using System.Threading.Tasks;
global using SeewoAutoLogin.Services;
global using SeewoAutoLogin.Ui;
