# MeshPresso — Voice To 3D

스페이스를 누르고 있는 동안 말한 내용으로 3D 모델을 생성해 씬에 배치하는 파이프라인입니다.
**API 키는 Genpresso 하나만 사용합니다** (텍스트·미디어 양쪽 모두 Genpresso 경유).

```
[Space 홀드 → 마이크 녹음(WAV)]
        ↓
[POST /api/v1/chat/completions · google/gemini-3.5-flash-lite]
        ↑ 오디오를 직접 듣고(input_audio) 영문 text-to-3D 프롬프트 생성
        ↓
[POST /api/v1/media/gp/hyper3d/rodin/v2.5/text-to-3d/fast → 폴링 → 결과]
        ↓
[모델/텍스처 다운로드 → AssetDatabase 임포트 → 지정한 위치에 배치]
```

## 설치

Unity 6000.0 이상.

**방법 A — Git URL (권장)**

Unity 에디터에서 `Window > Package Manager` → `+` → **Add package from git URL** →

```
https://github.com/Seonghoon-ban/meshpresso-voice-to-3d.git
```

버전을 고정하려면 뒤에 `#v0.1.0` 처럼 태그를 붙입니다.
`Packages/manifest.json`에 직접 적어도 됩니다:

```json
{
  "dependencies": {
    "com.meshpresso.voice-to-3d": "https://github.com/Seonghoon-ban/meshpresso-voice-to-3d.git"
  }
}
```

**의존 패키지는 자동으로 함께 설치됩니다.** 이 한 줄만 있으면 UPM이 레지스트리에서
`com.unity.cloud.gltfast`(GLB 임포트), `com.unity.nuget.newtonsoft-json`, `com.unity.inputsystem`을
알아서 받아옵니다. 빈 프로젝트에서 실제로 확인했습니다 — 별도로 설치할 것은 없습니다.

**방법 B — 폴더 복사**

`Packages/com.meshpresso.voice-to-3d` 폴더를 통째로 상대방 프로젝트의 `Packages/` 아래에 붙여넣습니다.
Unity가 embedded package로 인식합니다. 인터넷/깃 없이 전달할 때 가장 간단합니다.

**방법 C — tarball**

폴더를 `.tgz`로 압축해 전달하고, 받는 쪽에서 Package Manager → **Add package from tarball**.

> `.unitypackage` 내보내기는 권장하지 않습니다. 의존 패키지가 함께 딸려오지 않아
> 받는 쪽에서 컴파일 에러부터 보게 됩니다.

## API 키 발급

https://genpresso.ai/ko/developers 에서 발급합니다 (문서: https://genpresso.ai/ko/api).

- 키는 `gp_`로 시작하며 **발급 직후 한 번만 표시**되므로 그때 복사해 두세요.
- 텍스트·미디어 모두 이 키 하나로 호출합니다.
- **크레딧이 있어야 동작합니다.** 잔액이 부족하면 402가 돌아옵니다.
  대략 모델 1개 생성에 3크레딧, 프롬프트 생성은 0.001크레딧 미만입니다.
  실패한 요청은 과금되지 않습니다.

## 사용 방법

1. 메뉴 **MeshPresso > Voice To 3D > Setup Scene** 클릭
   - `Assets/MeshPresso/VoiceTo3DSettings.asset` 생성(폴더가 없으면 만들어 줍니다) + 씬에 `VoiceTo3DController` 추가
2. **VoiceTo3DSettings** 에셋의 **Genpresso Api Key**에 `gp_...` 키 입력
   - 비워두면 환경변수 `GENPRESSO_API_KEY`를 대신 사용합니다.
   - 프로젝트를 공유한다면 에셋에 넣지 말고 환경변수를 쓰세요.
3. (선택) `VoiceTo3DController`의 **Spawn Anchor**에 빈 GameObject를 지정 — 아래 참고.
4. **Play** 진입 → **스페이스를 누른 채** 만들고 싶은 물체를 말하고 → 손을 떼면 파이프라인이 실행됩니다.
   - 화면 좌상단 HUD에 상태(녹음 → 프롬프트 생성 → 생성 큐 → 다운로드 → 배치)가 표시됩니다.
   - 한국어로 말해도 됩니다. Gemini가 알아서 영문 프롬프트로 변환합니다.

## 생성 위치 지정 (Spawn Anchor)

`VoiceTo3DController`의 **Spawn Anchor**에 Transform을 지정하면 그 위치·회전에 모델이 배치됩니다.
비워두면 기존처럼 메인 카메라 앞 `Spawn Distance` 지점에 놓입니다.

- **포즈는 생성이 시작되는 순간(= 녹음이 끝나는 순간)에 스냅샷됩니다.**
  생성에 45초~2분이 걸리는 동안 앵커나 카메라가 움직여도 결과가 따라다니지 않습니다.
  여러 번 연속으로 말하면 각 생성이 자기 시작 시점의 위치를 각각 기억합니다.
- 기본적으로 모델의 **바운딩 박스 중심**이 앵커에 맞춰집니다.
  앵커를 바닥이나 책상 위에 두었다면 설정의 **Place Bottom At Spawn Point**를 켜세요.
  모델 밑면이 앵커에 놓이도록 바뀝니다.
- 앵커의 회전도 반영됩니다(임포터 축 변환 위에 합성).

## Genpresso API 연동 메모

실제 호출로 확인한 사항들입니다.

- **인증**: 모든 요청에 `Authorization: Bearer gp_...`. 텍스트/미디어 구분 없이 동일합니다.
- **텍스트**: `POST /api/v1/chat/completions` — OpenAI 규약 그대로. **오디오 입력(`input_audio`) 정상 동작 확인**
  (응답 `usage.prompt_tokens_details.audio_tokens`로 실제 오디오 처리 확인).
- **폴링 URL에는 모델 경로가 없습니다**: `media/requests/{id}/status`, `media/requests/{id}`.
  제출 응답의 `status_url` / `response_url` / `cancel_url`은 절대 URL로 오므로 그대로 사용하고,
  없을 때만 `request_id`로 조립합니다.
- **주의 1 — status가 거짓말을 합니다**: 요청이 검증에서 실패해도 status는 `COMPLETED`로 보고됩니다.
  실제 실패는 결과 조회에서 **422**로만 드러나므로, 결과 본문을 반드시 확인해야 합니다.
- **주의 2 — 결과가 상태보다 늦습니다**: status가 `COMPLETED`인데 결과가 잠깐
  `{"detail":"Request is still in progress"}`(200)로 오는 구간이 있어, 이 경우 재시도합니다.
- **에러 형태 2종**: OpenAI식 `{"error":{message,code,type}}` 와 FastAPI식 `{"detail":[{loc,msg}]}`.
  후자는 어느 필드가 왜 틀렸는지 알려주므로 그대로 파싱해 보여줍니다.
- **취소 상태 문자열은 `CANCELED`** (L 하나)입니다.
- 결과 파일 URL은 외부 CDN이고 만료되므로 즉시 내려받습니다.
- 미디어 제출은 최소 잔액 10크레딧, 텍스트는 1크레딧이 필요합니다.
  Rodin fast 1회 = 약 3크레딧, 프롬프트 생성 1회 = 0.001크레딧 미만.

### 검증된 파라미터 값

`tier` = `Gen-2.5-Minimum` / `Gen-2.5-Extreme-Low` / `Gen-2.5-Low`,
`geometry_file_format` = `glb` / `usdz` / `fbx` / `obj` / `stl`,
`material` = `PBR` / `Shaded` / `All` / `None`.
잘못된 값을 넣으면 422 응답이 필드별로 정확히 알려줍니다.

## 포맷별 차이 (glb vs fbx)

| | GLB (기본값) | FBX |
|---|---|---|
| 임포터 | glTFast (`com.unity.cloud.gltfast`) | Unity 내장 `FBXImporter` |
| 루트 회전 | (0, 0, 0) — 축 변환이 메시에 적용됨 | (270, 0, 0) — 임포터가 루트에 적용 |
| 텍스처 | glTFast가 정상 임포트, 별도 처리 불필요 | 내부 embed, **Unity가 자동으로 못 꺼냄** → 추출 필요 |
| 용량 | 작음 (JPEG, ~3-6MB) | 매우 큼 (4K PNG 포함, ~63MB) |

두 경로 모두 지원하며 코드가 자동 판별합니다. **용량과 텍스처 안정성 면에서 glb를 기본값으로 둡니다.**

### 텍스처 처리

Rodin은 FBX+PBR 조합에서 텍스처를 별도 파일이 아니라 **FBX 내부에 embed**해서 보냅니다
(diffuse/normal/roughness/metallic PNG 4장). FBX 안에 적힌 경로는 서버쪽 `/tmp/.../output.fbm/...` 절대경로라
**Unity는 임포트 시 이 텍스처들을 전혀 꺼내지 않습니다** — 실제 파일로 확인한 결과 임포트 직후 텍스처 에셋이 0개였습니다.

그래서 임포트 후 `ModelImporter.ExtractTextures`로 임베디드 텍스처를 꺼내고 모델을 재임포트합니다.
그러면 **FBX 자체 머티리얼이 basecolor + normal에 자동으로 다시 연결**됩니다(검증 완료).

- 재임포트 후에도 머티리얼에 base 텍스처가 없을 때만 URP Lit 머티리얼을 새로 만들어 덮어씁니다.
  base 텍스처 판정은 `_BaseMap` / `_MainTex` / `_BaseColorMap` / `baseColorTexture`(glTFast)를 모두 확인합니다.
- metallic/roughness는 파일로 추출되지만 Unity가 자동 연결하지 않습니다
  (Standard/URP Lit은 두 맵을 한 텍스처에 패킹해서 쓰기 때문). 필요하면 수동으로 연결하세요.
- `textures` 응답 배열에는 이미지가 아니라 **다른 포맷의 모델 파일(glb 등)이 들어오는 경우**가 있어, 이미지 확장자만 골라 받습니다.
- basecolor를 찾지 못하면 머티리얼을 만들지 않고 모델 자체 머티리얼을 그대로 둡니다
  (normal map이 albedo로 들어가 보라색으로 렌더되는 것보다 낫기 때문).
- 텍스처가 안 붙은 기존 생성물은 씬 오브젝트(또는 모델 에셋)를 선택하고
  **MeshPresso > Voice To 3D > Repair Selected Model Textures** 메뉴로 재생성 없이 고칠 수 있습니다.

### 방향(회전) 처리

Rodin FBX는 Z-up이라 **Unity의 FBX 임포터가 프리팹 루트에 자동으로 X -90°를 넣어 둡니다**(실측: 루트 회전 = (270,0,0)).
GLB는 glTFast가 축 변환을 메시에 적용하므로 루트 회전이 (0,0,0)입니다.

배치할 때 `transform.rotation`을 통째로 덮어쓰면 이 축 변환이 사라져 모델이 옆으로 눕습니다.
그래서 **임포터가 넣어 둔 회전을 유지한 채** 카메라를 향하는 yaw만 합성합니다.

- `Placement Rotation Euler`는 그 위에 추가로 얹는 보정값이며 기본은 **(0,0,0)** 입니다.
  임포터가 이미 포맷별로 올바르게 세워주므로 보통 건드릴 필요가 없고, 특정 모델만 틀어질 때만 사용하세요.
- FBX에 X -90을 직접 넣으면 임포터 회전과 겹쳐 **-180°가 되어 거꾸로 뒤집힙니다**.

## 그 외 동작

- 생성물은 `Assets/MeshPresso/Generated/<타임스탬프>/`에 모델 + 텍스처 + 머티리얼 +
  `genpresso_response.json`(원본 응답)으로 저장됩니다.
- 배치된 오브젝트는 카메라 정면 `Spawn Distance`(기본 2.5m) 지점에, 최대 치수가 `Target Size`(기본 1m)가 되도록 스케일되어 놓입니다.
- **플레이 모드에서 배치한 오브젝트는 플레이 종료 시 사라지는 것이 Unity 기본 동작이지만**,
  `Keep Placement After Play Mode`(기본 켜짐)가 켜져 있으면 플레이 종료 후 같은 위치에 자동으로 다시 배치됩니다(씬 저장 필요).
- 생성 시간은 Rodin fast 기준 보통 45초~2분입니다. 타임아웃되면 cancel을 호출해 낭비를 줄입니다.

## 제한사항

- 모델 임포트는 Unity 에디터에서만 가능합니다(`AssetDatabase` 사용).
  빌드된 플레이어에서 쓰려면 glTFast의 런타임 로딩 API(`GltfImport`)로 교체해야 합니다.
- API 키는 에셋에 평문 저장됩니다. **패키지를 배포하거나 저장소를 공유하기 전에
  `VoiceTo3DSettings.asset`의 키를 반드시 지우고** 환경변수 `GENPRESSO_API_KEY`를 쓰세요.
  설정 에셋은 `Assets/` 아래(패키지 바깥)에 있으므로 패키지 자체에는 키가 포함되지 않습니다.
- Unity 6000.2.6f2에서 검증했습니다. `package.json`의 최소 버전은 6000.0으로 두었지만
  그 아래 버전은 확인하지 않았습니다.

## 패키지 구조

```
Packages/com.meshpresso.voice-to-3d/
  package.json          의존 패키지 선언 (newtonsoft-json, gltfast, inputsystem, 내장 모듈)
  README.md / CHANGELOG.md
  Runtime/              MeshPresso.VoiceTo3D.asmdef + 파이프라인 전체
  Editor/               MeshPresso.VoiceTo3D.Editor.asmdef + 메뉴 도구
```

사용자 데이터는 패키지 바깥에 둡니다(설치된 패키지는 읽기 전용):

```
Assets/MeshPresso/VoiceTo3DSettings.asset   설정 (키 포함)
Assets/MeshPresso/Generated/<타임스탬프>/    생성물 — 설정의 Generated Folder로 변경 가능
```
