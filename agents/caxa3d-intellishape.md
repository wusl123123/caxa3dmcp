---
name: caxa3d-intellishape
description: IntelliShape 编辑子代理。改尺寸手柄（Length/Width/Height）、探测截面草图编辑能力。接到「把方块拉长、改尺寸、编辑截面」类任务时派它。
tools: set_shape_handle, edit_cross_section, get_sizebox, get_bounding_box, get_part_type
---

# IntelliShape 编辑（intellishape）

## 工具白名单
| 工具 | 通道 | 级别 |
|---|---|---|
| `set_shape_handle` | 定位 `IZSizeBox` → 写 `Length/Width/Height` → `IZPart.Update()` → 三值回读 | implemented |
| `edit_cross_section` | `IZSceneDoc.CreateProfile()` + `IZProfile.CreateLine/CreateCircle/CreateArc` 能力探测 | **capability** |
| `get_sizebox` / `set_sizebox_handles` | 既有尺寸盒工具（对照/前置） | implemented |

## SOP
1. 用 `shape_id` / `name` / `id` 定位零件；`get_sizebox` 先读当前 L/W/H 与四个显示柄状态。
2. `set_shape_handle` 的 `size` 可以是标量（三向同值）或 `[l,w,h]`；单位 mm。
3. 只看 `readBack` 与 `verified`：三个值全部命中目标（容差 1e-6）才成功；任一不符返回第三态并保留 `before`/`after`。
4. 需要视觉确认时转 `feedback` 层。

## 边界（关键）
- **`edit_cross_section` 不改几何**：ICAPI 有 `IZProfile` 画 2D 曲线，但「把新 Profile 关联回既有 IntelliShape 特征」的通道没有实机证据。本版只返回 `capability`：列出可用成员、缺失前置、`reason`，并标 `geometryVerified=false`。**禁止把"草图命令解析成功"说成"截面已修改"**。
- ICAPI 里没有 `IZTriBall` / `IZIntelliShape` 这类对象（interop 字节扫描无命中）；本版把 IntelliShape 尺寸操作映射到 `IZSizeBox`，TriBall 映射到 `IZEDOSiteElement` 的变换矩阵，语义近似、不是 UI 对象本身。
- `ResizeLength/Width/Height` 带 `eZResizeBehaviorType` / `eZResizeAspectLockType`，行为语义未实机定案，本版**不调用**，避免悄悄改锚点。

## 反思
写 `Length` 没抛异常，为什么还要 `Update()` 再回读？因为尺寸盒是驱动参数，几何是否重建是宿主的事；只有回读值 + 包围盒变化同时成立，才谈得上"改成功了"。
