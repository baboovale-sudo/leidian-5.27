# 内存加载改造说明

本版本已按官方 `OLAMemoryLoad` Demo 改为内存加载 OLA 插件。

## 主要变更

- 新增 `olaplug/MemoryLoad/MemoryLoad.cs`。
- 新增嵌入资源：
  - `olaplug/MemoryLoad/Resources/loader_x64.enc.bin`
  - `olaplug/OLAPlug_x64.dll`
- 新增 `OlaRuntime.cs`，统一创建 `OLAPlugServer`。
- 替换原来的 `new OLAPlugServer("OLA.dll")`。
- `.csproj` 已设置为 x64，并将插件 DLL 与 loader 作为 EmbeddedResource。

## 使用方式

业务代码不要再直接写：

```csharp
new OLAPlugServer("OLA.dll")
```

统一使用：

```csharp
OlaRuntime.Create()
```

## 注意

官方 Demo 使用 x64 loader，所以项目必须以 x64 运行。
