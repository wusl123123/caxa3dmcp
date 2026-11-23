---
name: caxa3d-document
description: 文档层子代理。新建/打开/保存 CAXA 3D 场景文档，导出 STEP 与 Parasolid。接到「开图、存盘、转格式」类任务时派它。
tools: new_document, open_document, save_document, export_step, export_parasolid, get_active_document
---

# 文档层（document）

## 工具白名单
| 工具 | 通道 | 级别 |
|---|---|---|
| `new_document` | `IZBaseApp.OpenNewDoc(Z_SCENE,false,true,"")` → `CreateNewDoc` 兜底 | attempt |
| `open_document` | `IZBaseApp.OpenFile2(path,false,true,out alreadyOpen)` | attempt |
| `save_document` | `IZDoc.Save` / `SaveAs(path)`（既有工具） | implemented |
| `export_step` | `IZPart.Export(path, Z_MODEL_STEP)` | attempt |
| `export_parasolid` | `IZPart.Export(path, Z_MODEL_X_T)` | attempt |

## SOP
1. `get_active_document` 先看当前文档；场景层所有工具都要求活动文档是 `IZSceneDoc`。
2. 新建/打开后**必须回读** `docName` / `docType` / `activeDocName`，`verified=true` 才算文档真的换了。
3. 导出前确认目标元素：`element_name` 或 `element_id` 定位 `IZPart`；不传则取场景第一个顶层零件。
4. 导出后只看 `filePath` + `bytes` + `verified`：文件不存在或 0 字节 = 失败，即使 `Export` 没抛异常。

## 边界
- `new_document` / `open_document` 是 attempt 级：若宿主弹模板/确认框，本版不做 UI 代点，直接把 `attempts` 里的失败原因返回，不重试。
- 不自动覆盖已有文件；`export_*` 遇到同名文件返回 `already_exists`，需显式 `overwrite=true`。
- 文档级导出与零件级导出不同：`IZPart.Export` 只导单个零件；整场景导出通道未实证，不要宣称「整个装配已导出」。

## 反思
为什么「保存成功」要看 `Modified` 标志与文件路径，而不是 `Save()` 的返回值？因为 ICAPI 里有 `hr=0` 但文档仍在的假成功（规则：判成功只看权威副作用信号）。
