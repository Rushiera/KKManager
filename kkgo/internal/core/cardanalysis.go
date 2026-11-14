package core

import (
	"bytes"
	"encoding/json"
	"strings"
)

// 分项分析 kind——与 C# CardAnalysis.Run 的 stepId 一致（扫描计划步骤 id：coord / detail / scene）。
const (
	// AnalysisCoord 服装槽位（人物卡七套 coordinate）。
	AnalysisCoord = "coord"
	// AnalysisDetail 卡片分析（整个数据区结构）。
	AnalysisDetail = "detail"
	// AnalysisScene 场景深度（sd 专用）。
	AnalysisScene = "scene"
)

// AnalysisOrder 缺省执行序——对齐 C# 扫描计划序（服装槽位 → 卡片分析 → 场景深度）。
var AnalysisOrder = []string{AnalysisCoord, AnalysisDetail, AnalysisScene}

// AnalysisResult 一份分项分析：kind + 与 C# System.Text.Json 同形状的紧凑 JSON。
type AnalysisResult struct {
	// Kind 分项（coord / detail / scene）。
	Kind string
	// JSON 序列化文本（驼峰键 · 紧凑 · 不转义非 ASCII——对齐 C# CamelCase + UnsafeRelaxedJsonEscaping）。
	JSON string
}

// AnalyzeCard 对一张卡片执行分项分析，返回序列化结果与被跳过项的说明。
// kinds 为空 = 全部适用项。判据对齐 C# CardAnalysis.Run：coord 仅人物卡；scene 仅场景卡；detail 全卡型。
func AnalyzeCard(path string, kinds []string) ([]AnalysisResult, []string, error) {
	st := ParseCardStructure(path)
	if st.Error != "" {
		return nil, nil, &AnalysisError{Kind: "structure", Message: st.Error}
	}
	if st.ImageEnd <= 0 {
		return nil, nil, &AnalysisError{Kind: "structure", Message: "图片区终点未知——无法解析数据区"}
	}
	filePath := path
	if st.FilePath != "" {
		filePath = st.FilePath
	}
	if len(kinds) == 0 {
		kinds = AnalysisOrder
	}

	out := make([]AnalysisResult, 0, len(kinds))
	skipped := make([]string, 0, len(kinds))
	for _, kind := range kinds {
		switch kind {
		case AnalysisCoord:
			if !isCharaCard(st.CardType) {
				skipped = append(skipped, kind+"：非人物卡（卡类型 "+orNoneText(st.CardType)+"——只有人物卡带 Coordinate 块）")
				continue
			}
			r := ReadCardCoordinate(filePath)
			if r.Error != "" {
				return nil, skipped, &AnalysisError{Kind: kind, Message: r.Error}
			}
			text, err := marshalCSharp(coordJSONOf(r))
			if err != nil {
				return nil, skipped, err
			}
			out = append(out, AnalysisResult{Kind: kind, JSON: text})
		case AnalysisDetail:
			d := ParseCardDetail(filePath, st.ImageEnd)
			text, err := marshalCSharp(detailJSONOf(d))
			if err != nil {
				return nil, skipped, err
			}
			out = append(out, AnalysisResult{Kind: kind, JSON: text})
		case AnalysisScene:
			if st.CardType != SceneCardType {
				skipped = append(skipped, kind+"：非场景卡（卡类型 "+orNoneText(st.CardType)+"——场景深度只对 sd 生效）")
				continue
			}
			s := ReadSceneInfo(filePath, st.ImageEnd)
			if s.Error != "" {
				return nil, skipped, &AnalysisError{Kind: kind, Message: s.Error}
			}
			// 逐份内嵌角色卡面图——与数据区图片清单对宽高 / 字节（同一遍 detail 扫描供图；对不上由 AttachCharaFaces 出声）
			s.AttachCharaFaces(ParseCardDetail(filePath, st.ImageEnd).Images)
			text, err := marshalCSharp(sceneJSONOf(s))
			if err != nil {
				return nil, skipped, err
			}
			out = append(out, AnalysisResult{Kind: kind, JSON: text})
		default:
			skipped = append(skipped, kind+"：未知分项（可选 coord / detail / scene）")
		}
	}
	return out, skipped, nil
}

// AnalysisError 分项分析失败（失败必须可见——带分项名）。
type AnalysisError struct {
	// Kind 出错的环节（structure / coord / detail / scene）。
	Kind string
	// Message 失败原因。
	Message string
}

// Error 实现 error 接口。
func (e *AnalysisError) Error() string {
	return e.Kind + ": " + e.Message
}

// isCharaCard 是否人物卡（只有人物卡有 Coordinate 块——对齐 C# CardAnalysis.IsChara：卡类型里含 "Chara"）。
func isCharaCard(cardType string) bool {
	return strings.Contains(cardType, "Chara")
}

// orNoneText 空串显示为「未识别」（仅用于跳过说明，不参与 JSON）。
func orNoneText(s string) string {
	if s == "" {
		return "未识别"
	}
	return s
}

// marshalCSharp 按 C# 序列化口径输出紧凑 JSON——驼峰键（结构体标签）/ 不转义非 ASCII 与 HTML 字符 / 无缩进。
func marshalCSharp(v any) (string, error) {
	var buf bytes.Buffer
	enc := json.NewEncoder(&buf)
	enc.SetEscapeHTML(false)
	if err := enc.Encode(v); err != nil {
		return "", err
	}
	return strings.TrimRight(buf.String(), "\n"), nil
}

// optStr 空串 → JSON null（C# 侧这些属性未赋值时不出现 ""）。
func optStr(s string) *string {
	if s == "" {
		return nil
	}
	v := s
	return &v
}

// setStr 未读到键 → JSON null；读到（含空串）→ JSON 字符串。
func setStr(s string, set bool) *string {
	if !set {
		return nil
	}
	v := s
	return &v
}

// strs 保证列表非 nil（C# 的 List 一律序列化为 []，不是 null）。
func strs(v []string) []string {
	if v == nil {
		return []string{}
	}
	return v
}

// int64s 保证 int64 列表非 nil。
func int64s(v []int64) []int64 {
	if v == nil {
		return []int64{}
	}
	return v
}

// ---------- detail 线格式（字段序 = C# CardDetail 系属性声明序） ----------

// detailJSON C# CardDetailResult 的序列化形状。
type detailJSON struct {
	CharName        *string      `json:"charName"`
	FaceImageSize   int64        `json:"faceImageSize"`
	FaceImageOffset int64        `json:"faceImageOffset"`
	Parts           []partJSON   `json:"parts"`
	ModRefs         []modRefJSON `json:"modRefs"`
	Images          []imageJSON  `json:"images"`
	Blocks          []blockJSON  `json:"blocks"`
	PluginNames     []string     `json:"pluginNames"`
	KkxOffsets      []int64      `json:"kkxOffsets"`
	ImageBytesTotal int64        `json:"imageBytesTotal"`
	Warnings        []string     `json:"warnings"`
}

// partJSON C# CardPart 的序列化形状。
type partJSON struct {
	Index int      `json:"index"`
	Id    int64    `json:"id"`
	Keys  []string `json:"keys"`
}

// modRefJSON C# CardModDetail 的序列化形状。
type modRefJSON struct {
	ModId      string  `json:"modId"`
	Property   *string `json:"property"`
	Slot       int     `json:"slot"`
	LocalSlot  int     `json:"localSlot"`
	CategoryNo int     `json:"categoryNo"`
	Author     *string `json:"author"`
	Website    *string `json:"website"`
	Name       *string `json:"name"`
}

// imageJSON C# CardImageInfo 的序列化形状。
type imageJSON struct {
	Index     int   `json:"index"`
	Offset    int64 `json:"offset"`
	Size      int64 `json:"size"`
	Width     int   `json:"width"`
	Height    int   `json:"height"`
	BitDepth  int   `json:"bitDepth"`
	ColorType int   `json:"colorType"`
	Chunks    int   `json:"chunks"`
	Ended     bool  `json:"ended"`
	IsFace    bool  `json:"isFace"`
}

// blockJSON C# CardBlockInfo 的序列化形状。
type blockJSON struct {
	Name    string  `json:"name"`
	Version *string `json:"version"`
	Pos     int64   `json:"pos"`
	Size    int64   `json:"size"`
}

// detailJSONOf 由解析结果构造 detail 线格式。
func detailJSONOf(d *CardDetailResult) detailJSON {
	w := detailJSON{
		CharName:        setStr(d.CharName, d.CharNameSet),
		FaceImageSize:   d.FaceImageSize,
		FaceImageOffset: d.FaceImageOffset,
		Parts:           make([]partJSON, 0, len(d.Parts)),
		ModRefs:         make([]modRefJSON, 0, len(d.ModRefs)),
		Images:          imagesJSONOf(d.Images),
		Blocks:          make([]blockJSON, 0, len(d.Blocks)),
		PluginNames:     strs(d.PluginNames),
		KkxOffsets:      int64s(d.KkxOffsets),
		ImageBytesTotal: d.ImageBytesTotal,
		Warnings:        strs(d.Warnings),
	}
	for _, p := range d.Parts {
		w.Parts = append(w.Parts, partJSON{Index: p.Index, Id: p.Id, Keys: strs(p.Keys)})
	}
	for _, m := range d.ModRefs {
		w.ModRefs = append(w.ModRefs, modRefJSON{
			ModId:      m.ModId,
			Property:   setStr(m.Property, m.PropertySet),
			Slot:       m.Slot,
			LocalSlot:  m.LocalSlot,
			CategoryNo: m.CategoryNo,
			Author:     setStr(m.Author, m.AuthorSet),
			Website:    setStr(m.Website, m.WebsiteSet),
			Name:       setStr(m.Name, m.NameSet),
		})
	}
	for _, b := range d.Blocks {
		w.Blocks = append(w.Blocks, blockJSON{
			Name:    b.Name,
			Version: setStr(b.Version, b.VersionSet),
			Pos:     b.Pos,
			Size:    b.Size,
		})
	}
	return w
}

// imagesJSONOf 内嵌图片清单的线格式（detail 与 scene 共用）。
func imagesJSONOf(images []CardImageInfo) []imageJSON {
	list := make([]imageJSON, 0, len(images))
	for _, img := range images {
		list = append(list, imageJSON{
			Index:     img.Index,
			Offset:    img.Offset,
			Size:      img.Size,
			Width:     img.Width,
			Height:    img.Height,
			BitDepth:  img.BitDepth,
			ColorType: img.ColorType,
			Chunks:    img.Chunks,
			Ended:     img.Ended,
			IsFace:    img.IsFace,
		})
	}
	return list
}

// ---------- coord 线格式（字段序 = C# CardCoordinate 系属性声明序） ----------

// coordJSON C# CardCoordinateResult 的序列化形状。
type coordJSON struct {
	FilePath     string       `json:"filePath"`
	Outfits      []outfitJSON `json:"outfits"`
	BlockVersion *string      `json:"blockVersion"`
	BlockSize    int64        `json:"blockSize"`
	Warnings     []string     `json:"warnings"`
	Error        *string      `json:"error"`
}

// outfitJSON C# CardCoordinateOutfit 的序列化形状。
type outfitJSON struct {
	Index        int             `json:"index"`
	TypeName     string          `json:"typeName"`
	Clothes      []clothesJSON   `json:"clothes"`
	Accessories  []accessoryJSON `json:"accessories"`
	EnableMakeup bool            `json:"enableMakeup"`
	MakeupText   string          `json:"makeupText"`
	Warnings     []string        `json:"warnings"`
}

// clothesJSON C# CardClothesPart 的序列化形状。
type clothesJSON struct {
	Index       int      `json:"index"`
	Slot        string   `json:"slot"`
	Id          int      `json:"id"`
	SleevesType int      `json:"sleevesType"`
	EmblemeId   int      `json:"emblemeId"`
	EmblemeId2  int      `json:"emblemeId2"`
	HideOptA    bool     `json:"hideOptA"`
	HideOptB    bool     `json:"hideOptB"`
	Colors      []string `json:"colors"`
}

// accessoryJSON C# CardAccessoryPart 的序列化形状。
type accessoryJSON struct {
	Index        int      `json:"index"`
	Type         int      `json:"type"`
	TypeName     string   `json:"typeName"`
	Id           int      `json:"id"`
	ParentKey    string   `json:"parentKey"`
	ParentName   string   `json:"parentName"`
	NoShake      bool     `json:"noShake"`
	HideCategory int      `json:"hideCategory"`
	Colors       []string `json:"colors"`
}

// coordJSONOf 由解析结果构造 coord 线格式。
func coordJSONOf(r *CardCoordinateResult) coordJSON {
	w := coordJSON{
		FilePath:     r.FilePath,
		Outfits:      make([]outfitJSON, 0, len(r.Outfits)),
		BlockVersion: setStr(r.BlockVersion, r.BlockVersionSet),
		BlockSize:    r.BlockSize,
		Warnings:     strs(r.Warnings),
		Error:        optStr(r.Error),
	}
	for _, o := range r.Outfits {
		outfit := outfitJSON{
			Index:        o.Index,
			TypeName:     o.TypeName,
			Clothes:      make([]clothesJSON, 0, len(o.Clothes)),
			Accessories:  make([]accessoryJSON, 0, len(o.Accessories)),
			EnableMakeup: o.EnableMakeup,
			MakeupText:   o.MakeupText,
			Warnings:     strs(o.Warnings),
		}
		for _, p := range o.Clothes {
			outfit.Clothes = append(outfit.Clothes, clothesJSON{
				Index:       p.Index,
				Slot:        p.Slot,
				Id:          p.Id,
				SleevesType: p.SleevesType,
				EmblemeId:   p.EmblemeId,
				EmblemeId2:  p.EmblemeId2,
				HideOptA:    p.HideOptA,
				HideOptB:    p.HideOptB,
				Colors:      strs(p.Colors),
			})
		}
		for _, p := range o.Accessories {
			outfit.Accessories = append(outfit.Accessories, accessoryJSON{
				Index:        p.Index,
				Type:         p.Type,
				TypeName:     p.TypeName,
				Id:           p.Id,
				ParentKey:    p.ParentKey,
				ParentName:   p.ParentName,
				NoShake:      p.NoShake,
				HideCategory: p.HideCategory,
				Colors:       strs(p.Colors),
			})
		}
		w.Outfits = append(w.Outfits, outfit)
	}
	return w
}

// ---------- scene 线格式（字段序 = C# SceneReader 系属性声明序） ----------

// sceneJSON C# SceneInfoResult 的序列化形状（internal 属性不参与序列化）。
type sceneJSON struct {
	Scanned        bool             `json:"scanned"`
	Error          *string          `json:"error"`
	Plugins        []sceneItemJSON  `json:"plugins"`
	CharaDataCount int              `json:"charaDataCount"`
	ScannedBytes   int64            `json:"scannedBytes"`
	KkpeItemCount  int              `json:"kkpeItemCount"`
	PropNames      []nameCountJSON  `json:"propNames"`
	SssbObjects    int              `json:"sssbObjects"`
	SssbCameras    int              `json:"sssbCameras"`
	CharaFaces     []imageJSON      `json:"charaFaces"`
	CharaData      []sceneCharaJSON `json:"charaData"`
	Warnings       []string         `json:"warnings"`
}

// sceneItemJSON C# ScenePluginItem 的序列化形状。
type sceneItemJSON struct {
	Key     string  `json:"key"`
	Version string  `json:"version"`
	Shape   string  `json:"shape"`
	Bytes   int64   `json:"bytes"`
	Offset  int64   `json:"offset"`
	Summary *string `json:"summary"`
}

// nameCountJSON C# SceneNameCount 的序列化形状。
type nameCountJSON struct {
	Name  string `json:"name"`
	Count int    `json:"count"`
}

// sceneCharaJSON C# SceneCharaData 的序列化形状。
type sceneCharaJSON struct {
	Index       int         `json:"index"`
	DataVersion *string     `json:"dataVersion"`
	FaceOffset  int64       `json:"faceOffset"`
	FaceSize    int64       `json:"faceSize"`
	Blocks      []blockJSON `json:"blocks"`
}

// sceneJSONOf 由解析结果构造 scene 线格式。
func sceneJSONOf(s *SceneInfoResult) sceneJSON {
	w := sceneJSON{
		Scanned:        s.Scanned,
		Error:          optStr(s.Error),
		Plugins:        make([]sceneItemJSON, 0, len(s.Plugins)),
		CharaDataCount: s.CharaDataCount,
		ScannedBytes:   s.ScannedBytes,
		KkpeItemCount:  s.KkpeItemCount,
		PropNames:      make([]nameCountJSON, 0, len(s.PropNames)),
		SssbObjects:    s.SssbObjects,
		SssbCameras:    s.SssbCameras,
		CharaFaces:     imagesJSONOf(s.CharaFaces),
		CharaData:      make([]sceneCharaJSON, 0, len(s.CharaData)),
		Warnings:       strs(s.Warnings),
	}
	for _, it := range s.Plugins {
		w.Plugins = append(w.Plugins, sceneItemJSON{
			Key:     it.Key,
			Version: it.Version,
			Shape:   it.Shape,
			Bytes:   it.Bytes,
			Offset:  it.Offset,
			Summary: optStr(it.Summary),
		})
	}
	for _, nc := range s.PropNames {
		w.PropNames = append(w.PropNames, nameCountJSON{Name: nc.Name, Count: nc.Count})
	}
	for _, chd := range s.CharaData {
		item := sceneCharaJSON{
			Index:       chd.Index,
			DataVersion: setStr(chd.DataVersion, chd.DataVersionSet),
			FaceOffset:  chd.FaceOffset,
			FaceSize:    chd.FaceSize,
			Blocks:      make([]blockJSON, 0, len(chd.Blocks)),
		}
		for _, b := range chd.Blocks {
			item.Blocks = append(item.Blocks, blockJSON{
				Name:    b.Name,
				Version: setStr(b.Version, b.VersionSet),
				Pos:     b.Pos,
				Size:    b.Size,
			})
		}
		w.CharaData = append(w.CharaData, item)
	}
	return w
}

// AnalyzeScanFile 对扫描内存态的一张卡片执行一个分项步——返回（结果 · 是否完成 · 错误）。
// done=true 表示该文件该步已完成（可落完成戳）；done=false 表示失败（不落戳，下次重试）。
// 对齐 C# CardAnalysis.Run：coord 仅人物卡、scene 仅场景卡（不适用视为已完成、不落数据）；
// detail 全卡型；ImageEnd / CardType 一律取扫描内存态（不重读头段）。
func AnalyzeScanFile(f *ScanFile, kind string) (*AnalysisResult, bool, error) {
	if f == nil {
		return nil, false, &AnalysisError{Kind: kind, Message: "扫描态为空"}
	}
	switch kind {
	case AnalysisCoord:
		if !isCharaCard(f.CardType) {
			return nil, true, nil
		}
		r := ReadCardCoordinate(f.Path)
		if r.Error != "" {
			return nil, false, &AnalysisError{Kind: kind, Message: r.Error}
		}
		text, err := marshalCSharp(coordJSONOf(r))
		if err != nil {
			return nil, false, err
		}
		return &AnalysisResult{Kind: kind, JSON: text}, true, nil
	case AnalysisDetail:
		d := ParseCardDetail(f.Path, f.ImageEnd)
		text, err := marshalCSharp(detailJSONOf(d))
		if err != nil {
			return nil, false, err
		}
		return &AnalysisResult{Kind: kind, JSON: text}, true, nil
	case AnalysisScene:
		if f.CardType != SceneCardType {
			return nil, true, nil
		}
		s := ReadSceneInfo(f.Path, f.ImageEnd)
		if s.Error != "" {
			return nil, false, &AnalysisError{Kind: kind, Message: s.Error}
		}
		// 逐份内嵌角色卡面图——与数据区图片清单对宽高 / 字节（同一遍 detail 扫描供图；对不上由 AttachCharaFaces 出声）
		s.AttachCharaFaces(ParseCardDetail(f.Path, f.ImageEnd).Images)
		text, err := marshalCSharp(sceneJSONOf(s))
		if err != nil {
			return nil, false, err
		}
		return &AnalysisResult{Kind: kind, JSON: text}, true, nil
	}
	return nil, false, &AnalysisError{Kind: kind, Message: "未知分项（可选 coord / detail / scene）"}
}
