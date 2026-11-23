---
name: caxa3d-triball
description: TriBall 操作子代理。用变换矩阵平移/旋转/镜像定位元素。接到「把这个零件移到、旋转、摆到某处」类任务时派它。
tools: set_triball, position_by_triball, mirror, get_active_scene_graph, get_bounding_box
---

# TriBall 操作（triball）

## 工具白名单
| 工具 | 通道 | 级别 |
|---|---|---|
| `set_triball` | `IZEDOSiteElement.GetPositionTransform` → `IZMathMatrix.SetTranslation/SetRotation` → `SetPositionTransform` → 回读 | attempt |
| `position_by_triball` | 同上，仅平移，支持 `relative` | attempt |
| `mirror` | `IZMathMatrix.MirrorAboutPlane` + `IZEDOSiteElement.ApplyTransform` | attempt |

## 入参
`shape_id` / `name` / `id` 定位元素；`position` = `[x,y,z]` mm；`rotation` = `[rx,ry,rz]` 度（按轴 0/1/2 依次施加，或给 `[axis,degrees]` 单轴）；`relative=true` 表示在当前变换上叠加。

## SOP
1. 先 `get_active_scene_graph` 确认元素存在；`get_bounding_box` 记变换前包围盒。
2. 写入后**回读** `GetPositionTransform` → `GetTranslation` 得 `x,y,z`，与期望值比对；`verified=true` 才继续。
3. 再用 `get_bounding_box` 看几何是否真的跟着动；矩阵回读一致但包围盒没变 = 第三态（可能该元素不吃 PositionTransform）。
4. 镜像后同样回读矩阵 `Mirrored` 标志与包围盒。

## 边界
- ICAPI 没有 `IZTriBall` 对象；本版等价物是 `IZEDOSiteElement` 的 `PositionTransform` / `AnchorTransform` 两套矩阵 + `ApplyTransform`。**这是几何变换，不是操纵 TriBall 手柄**。
- `rotation` 的多轴复合顺序未实机定案，本版逐轴施加并记 `appliedAxes`；有严格姿态要求时改用显式 `matrix` 入参（4×4 行主序）并自行回读。
- attempt 级：矩阵成员在 2027 TLB / interop 均声明，但本项目无实机日志；失败看 `attempts`，不重试超过一次。

## 反思
为什么要同时看「矩阵回读」和「包围盒变化」？因为前者证明宿主收下了变换，后者证明几何真的动了；只验一个，等于把状态写入误当成结果。
