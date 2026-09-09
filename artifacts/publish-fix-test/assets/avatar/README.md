# avatar 资产包 v0.1

## 目录
- sprites/        12张已抠底透明立绘(1254x1254,gen_00~11,画布未裁切以保证帧间对齐)
- stickers/       表情包贴纸(sweat_laugh = gen_10裁头,流汗黄豆位)
- dev_placeholder/ itch免费女仆sprite sheet(Idle 5帧/Run 8帧,单帧144x144),渲染器开发占位用
- avatar.json     状态机+口型+运动层+贴纸的全部配置
- contact_sheet.png 验收对照表,顺序 gen_00→11,每行4张

## 待办
1. 对照 contact_sheet 核对 avatar.json 里 _verify:true 的状态名,对不上改 file 字段
2. 无语表情走贴纸通道,后续生成的梗图PNG丢进 stickers/ 并在 json 注册
3. 渲染注意:整数倍缩放+NearestNeighbor仅对dev_placeholder必须;主立绘是高清像素风,平滑缩放即可
4. 首次在有色背景上渲染时检查发丝/腿缝间是否有残留白色小块(泛洪抠底不覆盖封闭区域),有则单独补抠
