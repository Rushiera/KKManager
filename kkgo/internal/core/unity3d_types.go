// Unity3d（AssetBundle）对象类型统计——按 classID 聚合 SerializedFile 对象表，
// 回答「这个包里有哪些资产」，是资产面侦察（P0）的最小判据。
//
// 复用 unity3d.go 的容器解包与 SerializedFile 解析；只读、不改包。
package core

import (
	"sort"
	"strconv"
)

// U3dTypeCount 单个 classID 的聚合结果。
type U3dTypeCount struct {
	// ClassID Unity classID。
	ClassID int
	// Name 类型名（内置表；未知 classID 给 "ClassID N"）。
	Name string
	// Count 该类型对象数。
	Count int
}

// u3dClassNames 常用 Unity classID → 类型名（覆盖 Unity 5.6 时代 KK 资产面）。
var u3dClassNames = map[int]string{
	1:    "GameObject",
	2:    "Component",
	3:    "LevelGameManager",
	4:    "Transform",
	5:    "TimeManager",
	20:   "Camera",
	21:   "Material",
	23:   "MeshRenderer",
	28:   "Texture2D",
	29:   "SceneSettings",
	33:   "MeshFilter",
	41:   "OcclusionCullingSettings",
	43:   "Mesh",
	48:   "Shader",
	49:   "TextAsset",
	54:   "Cubemap",
	64:   "MeshCollider",
	65:   "BoxCollider",
	74:   "AnimationClip",
	82:   "AudioClip",
	83:   "AudioSource",
	84:   "AudioListener",
	89:   "Cubemap",
	90:   "Avatar",
	91:   "AnimatorController",
	95:   "Animator",
	96:   "TrailRenderer",
	104:  "RenderSettings",
	108:  "Light",
	111:  "Animation",
	114:  "MonoBehaviour",
	115:  "MonoScript",
	120:  "LineRenderer",
	128:  "Font",
	135:  "SphereCollider",
	136:  "CapsuleCollider",
	137:  "SkinnedMeshRenderer",
	142:  "AssetBundle",
	150:  "PreloadData",
	157:  "LightmapSettings",
	196:  "NavMeshSettings",
	198:  "ParticleSystem",
	199:  "ParticleSystemRenderer",
	205:  "LODGroup",
	210:  "TerrainData",
	212:  "SpriteRenderer",
	213:  "Sprite",
	218:  "NavMeshData",
	222:  "CanvasRenderer",
	223:  "Canvas",
	224:  "RectTransform",
	225:  "CanvasGroup",
	1001: "Prefab",
}

// ListObjectTypes 按 classID 聚合包内对象表；返回聚合清单、对象总数、诊断文本（空串 = 成功）。
func ListObjectTypes(bundle []byte) ([]U3dTypeCount, int, string) {
	if len(bundle) < 64 {
		return nil, 0, "数据过短"
	}
	data, loadErr := unpackBundle(bundle)
	if loadErr != "" {
		return nil, 0, loadErr
	}
	_, types, objects, parseErr := parseSerializedFile(data)
	if parseErr != "" {
		return nil, 0, parseErr
	}
	counts := make(map[int]int)
	for _, o := range objects {
		cid := 0
		if o.TypeId >= 0 && o.TypeId < len(types) {
			cid = types[o.TypeId].ClassId
		}
		counts[cid]++
	}
	out := make([]U3dTypeCount, 0, len(counts))
	for cid, n := range counts {
		name := u3dClassNames[cid]
		if name == "" {
			name = "ClassID " + strconv.Itoa(cid)
		}
		out = append(out, U3dTypeCount{ClassID: cid, Name: name, Count: n})
	}
	sort.Slice(out, func(i, j int) bool {
		if out[i].Count != out[j].Count {
			return out[i].Count > out[j].Count
		}
		return out[i].ClassID < out[j].ClassID
	})
	return out, len(objects), ""
}
