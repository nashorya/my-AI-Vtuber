# avatar 资产包 v0.5

## 目录
- sprites/        12张已抠底透明立绘(1254x1254,gen_00~11,画布未裁切以保证帧间对齐)
- stickers/       表情包贴纸(sweat_laugh = gen_10裁头,流汗黄豆位)
- layered/        头身分层(二期呼吸跟随 + 歪头)
- dev_placeholder/ itch免费女仆sprite sheet(Idle 5帧/Run 8帧,单帧144x144),渲染器开发占位用
- avatar.json     状态机+口型+运动层+贴纸+分层的全部配置
- contact_sheet.png 验收对照表,顺序 gen_00→11,每行4张

## v0.5 分层资产
- `layered/head/gen_XX.png`  12个头层;切口 `cut_y=535`;**y535~545 线性羽化已烤进 PNG alpha**,渲染器不做任何边缘处理
- `layered/body.png`         身体层(整张);与头层画布对齐叠合
- `layers.cut_y`             呼吸跟随用: `头层位移 = (pivot_y - cut_y) × (scaleY - 1)`(脚底 `meta.pivot`)
- `layers.neck_pivot`        (627, 500);歪头仅旋转头层
- `layers.feather_to_y`      文档字段(545);代码不读、不处理
- `layers.enabled=false` 或资产缺失时须完整回退一期单图层模式

## 渲染注意
1. 主立绘平滑缩放即可;NearestNeighbor 仅对 `dev_placeholder` 必须
2. 禁止在代码里对头/身接缝做羽化、模糊或额外裁切——接缝软边以 PNG 为准
3. 首次在有色背景上渲染时检查发丝/腿缝间是否有残留白色小块
