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

## v0.3 分层资产(二期歪头用)
- layered/head/gen_XX.png  12个头层,切口 y=535(必须高于泡泡袖 y≈550)
- layered/body.png         身体层:y<535 为AI生成的头发填充(垫底用,单独看像无脸女鬼,属正常),y>=535 为原图身体
- 颈部支点 (627, 500);歪头仅旋转头层,身体层不动
- layers.enabled=false 或资产缺失时须完整回退一期单图层模式
