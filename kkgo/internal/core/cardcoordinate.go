// Package core — KKM 解析核（Go 重写 · 对照 C# 0.21.9）。
//
// 本文件：人物卡的服装 / 饰品读取（Coordinate 块的七套槽位：服装 9 槽 + 饰品 20 槽 + 化妆），
// 语义逐条对齐 KKManager/Core/CardCoordinate.cs：
//   - 结构依据游戏 ChaFileCoordinate.SaveBytes（clothes / accessory / enableMakeup / makeup 四段），
//     Coordinate 块本体是 MessagePack 数组，每套一个 bin；
//   - 逐段长度均为小端 int32；各段内部按 MessagePack map 读；
//   - 只读：不写文件、不改内容；失败具名（告警文本与 C# 逐句同形 —— 对拍判据）。
package core

import (
	"math"
	"os"
	"strconv"
	"strings"
)

// coordinateBlockName Coordinate 块名（lstInfo 目录条目）。
const coordinateBlockName = "Coordinate"

// coordinateMaxBlockBytes 块读取上限（防御性——正常 7 套约 70 KB）。
const coordinateMaxBlockBytes = 64 * 1024 * 1024

// clothesSlots 服装槽位名（顺序 = 游戏 ChaFileDefine.ClothesKind）。
var clothesSlots = []string{
	"上衣", "下装", "内衣（上）", "内衣（下）", "手套", "裤袜", "袜子", "鞋（内）", "鞋（外）",
}

// coordinateSlots Coordinate 槽位名（顺序 = 游戏 ChaFileDefine.CoordinateType）。
var coordinateSlots = []string{
	"学校制服①（School01）", "学校制服②（School02）", "体操服（Gym）", "泳装（Swim）",
	"社团（Club）", "便服（Plain）", "睡衣（Pajamas）",
}

// accessoryTypeNames 饰品类别名（游戏 ChaAccessoryDefine.AccessoryTypeName，值 = ChaListDefine.CategoryNo）。
var accessoryTypeNames = []string{
	"无（120）", "发（121）", "头（122）", "脸（123）", "颈（124）", "躯干（125）",
	"腰（126）", "腿（127）", "臂（128）", "手（129）", "股间（130）",
}

// accessoryParentKeys 饰品挂点骨骼名（顺序 = 游戏 ChaAccessoryDefine.AccessoryParentKey）。
var accessoryParentKeys = []string{
	"none", "a_n_hair_pony", "a_n_hair_twin_L", "a_n_hair_twin_R", "a_n_hair_pin", "a_n_hair_pin_R",
	"a_n_headtop", "a_n_headflont", "a_n_head", "a_n_headside", "a_n_earrings_L", "a_n_earrings_R",
	"a_n_megane", "a_n_nose", "a_n_mouth", "a_n_neck", "a_n_bust_f", "a_n_bust", "a_n_nip_L", "a_n_nip_R",
	"a_n_back", "a_n_back_L", "a_n_back_R", "a_n_waist", "a_n_waist_f", "a_n_waist_b", "a_n_waist_L", "a_n_waist_R",
	"a_n_leg_L", "a_n_knee_L", "a_n_ankle_L", "a_n_heel_L", "a_n_leg_R", "a_n_knee_R", "a_n_ankle_R", "a_n_heel_R",
	"a_n_shoulder_L", "a_n_elbo_L", "a_n_arm_L", "a_n_wrist_L", "a_n_shoulder_R", "a_n_elbo_R", "a_n_arm_R", "a_n_wrist_R",
	"a_n_hand_L", "a_n_ind_L", "a_n_mid_L", "a_n_ring_L", "a_n_hand_R", "a_n_ind_R", "a_n_mid_R", "a_n_ring_R",
	"a_n_dan", "a_n_kokan", "a_n_ana",
}

// accessoryParentNames 饰品挂点中文名（与 accessoryParentKeys 同序，取自游戏表）。
var accessoryParentNames = []string{
	"未设置", "马尾", "双马尾左", "双马尾右", "发夹左", "发夹右",
	"帽子", "额头", "头顶", "头侧", "耳环左", "耳环右",
	"眼镜", "鼻", "口", "颈", "胸上", "胸上中央", "左乳首", "右乳首",
	"背中中央", "背中左", "背中右", "腰", "腰前", "腰后", "腰左", "腰右",
	"左大腿", "左膝", "左脚踝", "左脚跟", "右大腿", "右膝", "右脚踝", "右脚跟",
	"左肩", "左肘", "左上臂", "左手腕", "右肩", "右肘", "右上臂", "右手腕",
	"左手", "左食指", "左中指", "左无名指", "右手", "右食指", "右中指", "右无名指",
	"男根根部", "女性器", "后穴",
}

// CardClothesPart 一件服装部件——某套 coordinate 里一个槽位的读数。
type CardClothesPart struct {
	// Index 槽位下标（0 起，顺序见 clothesSlots）。
	Index int
	// Slot 槽位名（上衣 / 下装 / …）。
	Slot string
	// Id 部件 id（0 = 该槽位没穿东西）。
	Id int
	// SleevesType 袖型（sleevesType）。
	SleevesType int
	// EmblemeId 徽章 id（第一枚）。
	EmblemeId int
	// EmblemeId2 徽章 id（第二枚）。
	EmblemeId2 int
	// HideOptA 隐藏开关一（hideOpt[0]）。
	HideOptA bool
	// HideOptB 隐藏开关二（hideOpt[1]）。
	HideOptB bool
	// Colors 四组颜色的展示文本（底色 / 图案号 / 平铺 / 图案色）。
	Colors []string
}

// CardAccessoryPart 一件饰品——某套 coordinate 里一个饰品槽位的读数。
type CardAccessoryPart struct {
	// Index 饰品槽位下标（0 起，共 20 个）。
	Index int
	// Type 饰品类别值（游戏 ChaListDefine.CategoryNo：120 无 · 121 发 · 122 头 …）。
	Type int
	// TypeName 饰品类别名。
	TypeName string
	// Id 饰品 id（0 = 该槽位空）。
	Id int
	// ParentKey 挂点骨骼名（a_n_headside 等）。
	ParentKey string
	// ParentKeySet parentKey 键是否读到（区分「未读到」与「读到空串」——C# 侧 null 与空串输出不同）。
	ParentKeySet bool
	// ParentName 挂点中文名。
	ParentName string
	// NoShake 是否不晃动（noShake）。
	NoShake bool
	// HideCategory 隐藏类别（hideCategory）。
	HideCategory int
	// Colors 四组颜色的展示文本。
	Colors []string
}

// CardCoordinateOutfit 一套 coordinate（游戏内的一个服装槽位）的读数。
type CardCoordinateOutfit struct {
	// Index 槽位下标（0 起，共 7 套）。
	Index int
	// TypeName 槽位名（学校制服① / 体操服 / …）。
	TypeName string
	// Clothes 服装部件（9 个槽位，含空槽）。
	Clothes []CardClothesPart
	// Accessories 饰品（20 个槽位，含空槽）。
	Accessories []CardAccessoryPart
	// EnableMakeup 是否启用化妆（enableMakeup）。
	EnableMakeup bool
	// MakeupText 化妆 id 摘要（眼影 / 脸颊 / 口红 / 涂装两项）。
	MakeupText string
	// Warnings 这套 coordinate 的读取告警（结构不符时的具名出声）。
	Warnings []string
}

// CardCoordinateResult 人物卡 coordinate（服装 / 饰品）读取结果——只读快照。
type CardCoordinateResult struct {
	// FilePath 卡片文件绝对路径。
	FilePath string
	// Outfits 七套 coordinate（卡里读到的顺序）。
	Outfits []CardCoordinateOutfit
	// BlockVersion Coordinate 块的版本（lstInfo 里的 version）。
	BlockVersion string
	// BlockVersionSet version 键是否读到（区分「未读到」与「读到空串」）。
	BlockVersionSet bool
	// BlockSize Coordinate 块字节数。
	BlockSize int64
	// Warnings 解析告警（不静默）。
	Warnings []string
	// Error 读取错误（空 = 读到了）。
	Error string
}

// ClothesCount 该套 coordinate 里非空服装件数（id ≠ 0）。
func (o *CardCoordinateOutfit) ClothesCount() int {
	n := 0
	for i := range o.Clothes {
		if o.Clothes[i].Id != 0 {
			n++
		}
	}
	return n
}

// AccessoryCount 该套 coordinate 里非空饰品件数（id ≠ 0）。
func (o *CardCoordinateOutfit) AccessoryCount() int {
	n := 0
	for i := range o.Accessories {
		if o.Accessories[i].Id != 0 {
			n++
		}
	}
	return n
}

// ClothesSlotName 服装槽位名（下标越界时返回「槽位 N」）。
func ClothesSlotName(index int) string {
	if index >= 0 && index < len(clothesSlots) {
		return clothesSlots[index]
	}
	return "槽位 " + strconv.Itoa(index)
}

// OutfitName Coordinate 槽位名（下标越界时返回「第 N 套」）。
func OutfitName(index int) string {
	if index >= 0 && index < len(coordinateSlots) {
		return coordinateSlots[index]
	}
	return "第 " + strconv.Itoa(index+1) + " 套"
}

// AccessoryTypeName 饰品类别名（表外取值如实标号，不猜名）。
func AccessoryTypeName(t int) string {
	if t >= 120 && t-120 < len(accessoryTypeNames) {
		return accessoryTypeNames[t-120]
	}
	return "未收录（" + strconv.Itoa(t) + "）"
}

// AccessoryParentName 饰品挂点中文名（表外原样呈现骨骼名）。
func AccessoryParentName(key string) string {
	if key == "" || key == "none" {
		return "未设置"
	}
	for i := range accessoryParentKeys {
		if accessoryParentKeys[i] == key {
			return accessoryParentNames[i]
		}
	}
	return key
}

// ReadCardCoordinate 读取人物卡的服装 / 饰品（Coordinate 块）。只读；失败具名返回。
func ReadCardCoordinate(path string) *CardCoordinateResult {
	result := &CardCoordinateResult{FilePath: path}
	if _, err := os.Stat(path); err != nil {
		result.Error = "卡片文件不存在：" + path
		return result
	}
	layout := ParseCardLayout(path)
	if layout.Error != "" {
		// 复用编辑面的布局解析——非人物卡时换成服装语境的说法
		if layout.CardType != "" && !containsChara(layout.CardType) {
			result.Error = "不是人物卡（卡类型 " + layout.CardType + "）——服装 / 饰品只在人物卡的 Coordinate 块里"
		} else {
			result.Error = layout.Error
		}
		return result
	}
	block := findLayoutBlock(layout, coordinateBlockName)
	if block == nil {
		result.Error = "卡片里没有 " + coordinateBlockName + " 块（现有块：" + layoutBlockNames(layout) + "）"
		return result
	}
	result.BlockVersion = block.Version
	result.BlockVersionSet = block.VersionSet
	result.BlockSize = block.Size
	if block.Size <= 0 || block.Size > coordinateMaxBlockBytes {
		result.Error = coordinateBlockName + " 块大小异常（" + itoa(block.Size) + " 字节）"
		return result
	}
	f, err := os.Open(path)
	if err != nil {
		result.Error = "读取 " + coordinateBlockName + " 块失败：" + err.Error()
		return result
	}
	defer f.Close()
	buf := make([]byte, block.Size)
	if !readFullAt(f, layout.PayloadStart+block.Pos, buf) {
		result.Error = coordinateBlockName + " 块读取不完整（应 " + itoa(block.Size) + " 字节）"
		return result
	}
	parseOutfits(buf, result)
	return result
}

// findLayoutBlock 按名字取块（找不到返回 nil）。
func findLayoutBlock(layout *CardLayout, name string) *CardEditBlock {
	for i := range layout.Blocks {
		if layout.Blocks[i].Name == name {
			return &layout.Blocks[i]
		}
	}
	return nil
}

// layoutBlockNames 现有块名（出声用）。
func layoutBlockNames(layout *CardLayout) string {
	parts := make([]string, 0, len(layout.Blocks))
	for i := range layout.Blocks {
		parts = append(parts, layout.Blocks[i].Name)
	}
	return strings.Join(parts, " / ")
}

// parseOutfits 解析 Coordinate 块——MessagePack 数组，每套是一个 bin（ChaFileCoordinate.SaveBytes 的字节）。
func parseOutfits(buf []byte, result *CardCoordinateResult) {
	pos := 0
	count, ok := readCoordArrayHeader(buf, &pos)
	if !ok {
		head := "空"
		if len(buf) > 0 {
			head = "0x" + strings.ToUpper(strconv.FormatUint(uint64(buf[0]), 16))
			if len(head) == 3 {
				head = "0x0" + head[2:]
			}
		}
		result.Error = coordinateBlockName + " 块不是 MessagePack 数组（首字节 " + head + "）"
		return
	}
	for i := 0; i < count; i++ {
		outfit := CardCoordinateOutfit{Index: i, TypeName: OutfitName(i)}
		result.Outfits = append(result.Outfits, outfit)
		n := len(result.Outfits) - 1
		l, ok := readCoordBinHeader(buf, &pos)
		if !ok {
			result.Outfits[n].Warnings = append(result.Outfits[n].Warnings,
				"本套不是 MessagePack bin（偏移 "+itoa(int64(pos))+"）——本套及后续跳过")
			return
		}
		if l < 0 || int64(pos)+int64(l) > int64(len(buf)) {
			result.Outfits[n].Warnings = append(result.Outfits[n].Warnings,
				"本套字节数异常（声明 "+itoa(int64(l))+" · 剩余 "+itoa(int64(len(buf)-pos))+"）——本套跳过")
			return
		}
		parseOutfitBlob(buf, pos, l, &result.Outfits[n])
		pos += l
	}
	if pos < len(buf) {
		result.Warnings = append(result.Warnings,
			"块内还有 "+itoa(int64(len(buf)-pos))+" 字节未解析（超出 "+itoa(int64(count))+" 套）")
	}
}

// parseOutfitBlob 解析一套 coordinate 的字节——服装段 / 饰品段 / 化妆开关 / 化妆段。
func parseOutfitBlob(buf []byte, at, length int, outfit *CardCoordinateOutfit) {
	end := at + length
	p := at
	clothesLen, ok := readCoordInt32(buf, &p, end)
	if !ok {
		outfit.Warnings = append(outfit.Warnings, "缺少服装段长度")
		return
	}
	if clothesLen < 0 || p+clothesLen > end {
		outfit.Warnings = append(outfit.Warnings, "服装段长度异常（"+itoa(int64(clothesLen))+"）")
		return
	}
	parseClothes(buf, p, clothesLen, outfit)
	p += clothesLen

	accessoryLen, ok := readCoordInt32(buf, &p, end)
	if !ok {
		outfit.Warnings = append(outfit.Warnings, "缺少饰品段长度")
		return
	}
	if accessoryLen < 0 || p+accessoryLen > end {
		outfit.Warnings = append(outfit.Warnings, "饰品段长度异常（"+itoa(int64(accessoryLen))+"）")
		return
	}
	parseAccessory(buf, p, accessoryLen, outfit)
	p += accessoryLen

	if p >= end {
		outfit.Warnings = append(outfit.Warnings, "缺少化妆开关字节")
		return
	}
	outfit.EnableMakeup = buf[p] != 0
	p++

	makeupLen, ok := readCoordInt32(buf, &p, end)
	if !ok {
		outfit.Warnings = append(outfit.Warnings, "缺少化妆段长度")
		return
	}
	if makeupLen < 0 || p+makeupLen > end {
		outfit.Warnings = append(outfit.Warnings, "化妆段长度异常（"+itoa(int64(makeupLen))+"）")
		return
	}
	parseMakeup(buf, p, makeupLen, outfit)
}

// parseClothes 解析服装段（ChaFileClothes：version / parts[9] / subPartsId / hideBraOpt / hideShortsOpt）。
func parseClothes(buf []byte, at, length int, outfit *CardCoordinateOutfit) {
	cur := &msgCursor{buf: buf, pos: at}
	mapCount, ok := cur.tryReadMapHeader()
	if !ok {
		outfit.Warnings = append(outfit.Warnings, "服装段不是 MessagePack map")
		return
	}
	for i := 0; i < mapCount; i++ {
		key, ok := cur.tryReadString()
		if !ok {
			outfit.Warnings = append(outfit.Warnings, "服装段键读取失败（第 "+strconv.Itoa(i+1)+" 项）")
			return
		}
		if key == "parts" {
			readClothesParts(cur, outfit)
			continue
		}
		if !cur.trySkipValue() {
			outfit.Warnings = append(outfit.Warnings, "服装段跳过值失败（键 "+key+"）")
			return
		}
	}
	consumed := cur.pos - at
	if consumed != length {
		outfit.Warnings = append(outfit.Warnings,
			"服装段长度不符（声明 "+itoa(int64(length))+" · 实读 "+itoa(int64(consumed))+"）")
	}
}

// readClothesParts 解析服装部件数组（9 个槽位）。
func readClothesParts(cur *msgCursor, outfit *CardCoordinateOutfit) {
	count, ok := cur.tryReadArrayHeader()
	if !ok {
		outfit.Warnings = append(outfit.Warnings, "服装 parts 不是数组")
		return
	}
	for i := 0; i < count; i++ {
		part := CardClothesPart{Index: i, Slot: ClothesSlotName(i)}
		if !readClothesPart(cur, &part) {
			outfit.Warnings = append(outfit.Warnings, "第 "+strconv.Itoa(i+1)+" 件服装读取中断")
			return
		}
		outfit.Clothes = append(outfit.Clothes, part)
	}
	if count != len(clothesSlots) {
		outfit.Warnings = append(outfit.Warnings,
			"服装槽位数与预期不符（读到 "+strconv.Itoa(count)+" · 预期 "+strconv.Itoa(len(clothesSlots))+"）")
	}
}

// readClothesPart 读一件服装部件——id / colorInfo[4] / emblemeId / hideOpt / sleevesType。
func readClothesPart(cur *msgCursor, part *CardClothesPart) bool {
	mapCount, ok := cur.tryReadMapHeader()
	if !ok {
		return false
	}
	for i := 0; i < mapCount; i++ {
		key, ok := cur.tryReadString()
		if !ok {
			return false
		}
		switch key {
		case "id", "sleevesType", "emblemeId", "emblemeId2":
			n, ok := cur.tryReadLong()
			if !ok {
				return false
			}
			switch key {
			case "id":
				part.Id = int(n)
			case "sleevesType":
				part.SleevesType = int(n)
			case "emblemeId":
				part.EmblemeId = int(n)
			default:
				part.EmblemeId2 = int(n)
			}
		case "hideOpt":
			if !readHideOpt(cur, part) {
				return false
			}
		case "colorInfo":
			if !readColors(cur, &part.Colors) {
				return false
			}
		default:
			if !cur.trySkipValue() {
				return false
			}
		}
	}
	return true
}

// readHideOpt 读服装的隐藏开关（hideOpt 两项）。
func readHideOpt(cur *msgCursor, part *CardClothesPart) bool {
	count, ok := cur.tryReadArrayHeader()
	if !ok {
		return false
	}
	for i := 0; i < count; i++ {
		v, ok := cur.tryReadBool()
		if !ok {
			return false
		}
		if i == 0 {
			part.HideOptA = v
		} else if i == 1 {
			part.HideOptB = v
		}
	}
	return true
}

// readColors 读一组颜色（4 个 ColorInfo：底色 / 图案号 / 平铺 / 图案色）。
func readColors(cur *msgCursor, colors *[]string) bool {
	count, ok := cur.tryReadArrayHeader()
	if !ok {
		return false
	}
	for i := 0; i < count; i++ {
		text, ok := readColorInfo(cur)
		if !ok {
			return false
		}
		*colors = append(*colors, text)
	}
	return true
}

// readColorInfo 读一个 ColorInfo（baseColor / pattern / tiling / patternColor）。
func readColorInfo(cur *msgCursor) (string, bool) {
	mapCount, ok := cur.tryReadMapHeader()
	if !ok {
		return "", false
	}
	baseColor, patternColor, tiling, pattern := "", "", "", ""
	hasBase, hasPatternColor, hasTiling, hasPattern := false, false, false, false
	for i := 0; i < mapCount; i++ {
		key, ok := cur.tryReadString()
		if !ok {
			return "", false
		}
		switch key {
		case "baseColor":
			v, ok := readColorValue(cur)
			if !ok {
				return "", false
			}
			baseColor, hasBase = v, true
		case "patternColor":
			v, ok := readColorValue(cur)
			if !ok {
				return "", false
			}
			patternColor, hasPatternColor = v, true
		case "tiling":
			v, ok := readTiling(cur)
			if !ok {
				return "", false
			}
			tiling, hasTiling = v, true
		case "pattern":
			n, ok := cur.tryReadLong()
			if !ok {
				return "", false
			}
			pattern, hasPattern = strconv.FormatInt(n, 10), true
		default:
			if !cur.trySkipValue() {
				return "", false
			}
		}
	}
	text := "底色 " + coordOr(hasBase, baseColor) +
		" · 图案 " + coordOr(hasPattern, pattern) +
		" · 平铺 " + coordOr(hasTiling, tiling) +
		" · 图案色 " + coordOr(hasPatternColor, patternColor)
	return text, true
}

// coordOr 有值给值，无值给「?」（对齐 C# 的 null → "?"）。
func coordOr(has bool, v string) string {
	if !has {
		return "?"
	}
	return v
}

// readColorValue 读一个颜色值（Color 序列化为 4 个浮点）——转成 #RRGGBB 文本。
func readColorValue(cur *msgCursor) (string, bool) {
	rgba, ok := readDoubles(cur, 3)
	if !ok {
		return "", false
	}
	return hexColor(rgba[0], rgba[1], rgba[2]), true
}

// readTiling 读平铺（Vector2 两个浮点）。
func readTiling(cur *msgCursor) (string, bool) {
	xy, ok := readDoubles(cur, 2)
	if !ok {
		return "", false
	}
	return formatCoord(xy[0]) + "×" + formatCoord(xy[1]), true
}

// readDoubles 读一个浮点数组（至少 want 项；多出的照样消费）。
func readDoubles(cur *msgCursor, want int) ([]float64, bool) {
	count, ok := cur.tryReadArrayHeader()
	if !ok || count < want {
		return nil, false
	}
	all := make([]float64, count)
	for i := 0; i < count; i++ {
		v, ok := cur.tryReadDouble()
		if !ok {
			return nil, false
		}
		all[i] = v
	}
	return all, true
}

// formatCoord 浮点显示（最多三位小数，去尾零）。
func formatCoord(v float64) string {
	s := strconv.FormatFloat(v, 'f', 3, 64)
	if strings.Contains(s, ".") {
		s = strings.TrimRight(s, "0")
		s = strings.TrimSuffix(s, ".")
	}
	return s
}

// hexColor 颜色文本（#RRGGBB，分量裁到 0–255）。
func hexColor(r, g, b float64) string {
	return "#" + channelHex(r) + channelHex(g) + channelHex(b)
}

// channelHex 单个分量 → 两位大写十六进制。
func channelHex(v float64) string {
	s := strings.ToUpper(strconv.FormatInt(int64(coordChannel(v)), 16))
	if len(s) == 1 {
		s = "0" + s
	}
	return s
}

// coordChannel 浮点分量 → 0–255 整数（越界裁剪；.5 取偶，对齐 .NET Math.Round 默认）。
func coordChannel(value float64) int {
	n := int(math.RoundToEven(value * 255.0))
	if n < 0 {
		n = 0
	}
	if n > 255 {
		n = 255
	}
	return n
}

// parseAccessory 解析饰品段（ChaFileAccessory：version / parts[20]）。
func parseAccessory(buf []byte, at, length int, outfit *CardCoordinateOutfit) {
	cur := &msgCursor{buf: buf, pos: at}
	mapCount, ok := cur.tryReadMapHeader()
	if !ok {
		outfit.Warnings = append(outfit.Warnings, "饰品段不是 MessagePack map")
		return
	}
	for i := 0; i < mapCount; i++ {
		key, ok := cur.tryReadString()
		if !ok {
			outfit.Warnings = append(outfit.Warnings, "饰品段键读取失败（第 "+strconv.Itoa(i+1)+" 项）")
			return
		}
		if key == "parts" {
			readAccessoryParts(cur, outfit)
			continue
		}
		if !cur.trySkipValue() {
			outfit.Warnings = append(outfit.Warnings, "饰品段跳过值失败（键 "+key+"）")
			return
		}
	}
	consumed := cur.pos - at
	if consumed != length {
		outfit.Warnings = append(outfit.Warnings,
			"饰品段长度不符（声明 "+itoa(int64(length))+" · 实读 "+itoa(int64(consumed))+"）")
	}
}

// readAccessoryParts 解析饰品数组（20 个槽位）。
func readAccessoryParts(cur *msgCursor, outfit *CardCoordinateOutfit) {
	const wantAccessories = 20
	count, ok := cur.tryReadArrayHeader()
	if !ok {
		outfit.Warnings = append(outfit.Warnings, "饰品 parts 不是数组")
		return
	}
	for i := 0; i < count; i++ {
		part := CardAccessoryPart{Index: i}
		if !readAccessoryPart(cur, &part) {
			outfit.Warnings = append(outfit.Warnings, "第 "+strconv.Itoa(i+1)+" 件饰品读取中断")
			return
		}
		outfit.Accessories = append(outfit.Accessories, part)
	}
	if count != wantAccessories {
		outfit.Warnings = append(outfit.Warnings,
			"饰品槽位数与预期不符（读到 "+strconv.Itoa(count)+" · 预期 "+strconv.Itoa(wantAccessories)+"）")
	}
}

// readAccessoryPart 读一件饰品——type / id / parentKey / color[4] / hideCategory / noShake。
func readAccessoryPart(cur *msgCursor, part *CardAccessoryPart) bool {
	mapCount, ok := cur.tryReadMapHeader()
	if !ok {
		return false
	}
	for i := 0; i < mapCount; i++ {
		key, ok := cur.tryReadString()
		if !ok {
			return false
		}
		switch key {
		case "type", "id", "hideCategory":
			n, ok := cur.tryReadLong()
			if !ok {
				return false
			}
			switch key {
			case "type":
				part.Type = int(n)
				part.TypeName = AccessoryTypeName(part.Type)
			case "id":
				part.Id = int(n)
			default:
				part.HideCategory = int(n)
			}
		case "noShake":
			v, ok := cur.tryReadBool()
			if !ok {
				return false
			}
			part.NoShake = v
		case "parentKey":
			v, ok := cur.tryReadString()
			if !ok {
				return false
			}
			part.ParentKey = v
			part.ParentKeySet = true
			part.ParentName = AccessoryParentName(v)
		case "color":
			if !readAccessoryColors(cur, part) {
				return false
			}
		default:
			if !cur.trySkipValue() {
				return false
			}
		}
	}
	return true
}

// readAccessoryColors 读饰品的四组颜色。
func readAccessoryColors(cur *msgCursor, part *CardAccessoryPart) bool {
	count, ok := cur.tryReadArrayHeader()
	if !ok {
		return false
	}
	for i := 0; i < count; i++ {
		text, ok := readColorValue(cur)
		if !ok {
			return false
		}
		part.Colors = append(part.Colors, text)
	}
	return true
}

// parseMakeup 解析化妆段（ChaFileMakeup：眼影 / 脸颊 / 口红 / 涂装两项）。
func parseMakeup(buf []byte, at, length int, outfit *CardCoordinateOutfit) {
	cur := &msgCursor{buf: buf, pos: at}
	mapCount, ok := cur.tryReadMapHeader()
	if !ok {
		outfit.Warnings = append(outfit.Warnings, "化妆段不是 MessagePack map")
		return
	}
	eyeshadow, cheek, lip := -1, -1, -1
	var paint []int
	for i := 0; i < mapCount; i++ {
		key, ok := cur.tryReadString()
		if !ok {
			outfit.Warnings = append(outfit.Warnings, "化妆段键读取失败（第 "+strconv.Itoa(i+1)+" 项）")
			return
		}
		switch key {
		case "eyeshadowId", "cheekId", "lipId":
			n, ok := cur.tryReadLong()
			if !ok {
				name := "眼影"
				if key == "cheekId" {
					name = "脸颊"
				} else if key == "lipId" {
					name = "口红"
				}
				outfit.Warnings = append(outfit.Warnings, "化妆段"+name+"读取失败")
				return
			}
			switch key {
			case "eyeshadowId":
				eyeshadow = int(n)
			case "cheekId":
				cheek = int(n)
			default:
				lip = int(n)
			}
		case "paintId":
			count, ok := cur.tryReadArrayHeader()
			if !ok {
				outfit.Warnings = append(outfit.Warnings, "化妆段涂装读取失败")
				return
			}
			for k := 0; k < count; k++ {
				n, ok := cur.tryReadLong()
				if !ok {
					outfit.Warnings = append(outfit.Warnings, "化妆段涂装项读取失败")
					return
				}
				paint = append(paint, int(n))
			}
		default:
			if !cur.trySkipValue() {
				outfit.Warnings = append(outfit.Warnings, "化妆段跳过值失败（键 "+key+"）")
				return
			}
		}
	}
	var sb strings.Builder
	sb.WriteString("眼影 " + makeupText(eyeshadow))
	sb.WriteString(" · 脸颊 " + makeupText(cheek))
	sb.WriteString(" · 口红 " + makeupText(lip))
	sb.WriteString(" · 涂装 ")
	if len(paint) == 0 {
		sb.WriteString("—")
	} else {
		for i := range paint {
			if i > 0 {
				sb.WriteString("/")
			}
			sb.WriteString(strconv.Itoa(paint[i]))
		}
	}
	outfit.MakeupText = sb.String()
	consumed := cur.pos - at
	if consumed != length {
		outfit.Warnings = append(outfit.Warnings,
			"化妆段长度不符（声明 "+itoa(int64(length))+" · 实读 "+itoa(int64(consumed))+"）")
	}
}

// makeupText 读到的整数文本（-1 = 没读到）。
func makeupText(value int) string {
	if value < 0 {
		return "—"
	}
	return strconv.Itoa(value)
}

// readCoordInt32 读小端 32 位整数（Coordinate 分段长度）。
func readCoordInt32(buf []byte, pos *int, end int) (int, bool) {
	if *pos < 0 || *pos+4 > end || *pos+4 > len(buf) {
		return 0, false
	}
	v := int(uint32(buf[*pos]) | uint32(buf[*pos+1])<<8 | uint32(buf[*pos+2])<<16 | uint32(buf[*pos+3])<<24)
	*pos += 4
	return v, true
}

// readCoordArrayHeader 读 MessagePack 数组头（fixarray / array16 / array32）。
func readCoordArrayHeader(buf []byte, pos *int) (int, bool) {
	if *pos >= len(buf) {
		return 0, false
	}
	b := buf[*pos]
	if b >= 0x90 && b <= 0x9F {
		*pos++
		return int(b - 0x90), true
	}
	if b == 0xDC && *pos+3 <= len(buf) {
		n := int(buf[*pos+1])<<8 | int(buf[*pos+2])
		*pos += 3
		return n, true
	}
	if b == 0xDD && *pos+5 <= len(buf) {
		n := int(buf[*pos+1])<<24 | int(buf[*pos+2])<<16 | int(buf[*pos+3])<<8 | int(buf[*pos+4])
		*pos += 5
		return n, true
	}
	return 0, false
}

// readCoordBinHeader 读 MessagePack bin 头（bin8 / bin16 / bin32）。
func readCoordBinHeader(buf []byte, pos *int) (int, bool) {
	if *pos >= len(buf) {
		return 0, false
	}
	b := buf[*pos]
	if b == 0xC4 && *pos+2 <= len(buf) {
		n := int(buf[*pos+1])
		*pos += 2
		return n, true
	}
	if b == 0xC5 && *pos+3 <= len(buf) {
		n := int(buf[*pos+1])<<8 | int(buf[*pos+2])
		*pos += 3
		return n, true
	}
	if b == 0xC6 && *pos+5 <= len(buf) {
		n := int(buf[*pos+1])<<24 | int(buf[*pos+2])<<16 | int(buf[*pos+3])<<8 | int(buf[*pos+4])
		*pos += 5
		return n, true
	}
	return 0, false
}
