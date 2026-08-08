# Changelog

All notable changes to this package are documented here.
This project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0] - 2026-08-09

첫 배포 버전.

### Added
- 스페이스 홀드 음성 녹음 → 프롬프트 생성 → 3D 모델 생성 → 씬 배치 전체 파이프라인.
- Genpresso API 키 **하나로** 텍스트(chat/completions)와 미디어(Rodin text-to-3D) 양쪽 처리.
  키를 비워두면 환경변수 `GENPRESSO_API_KEY`를 사용합니다.
- `VoiceTo3DController.spawnAnchor`: 생성 시작 시점의 Transform 포즈를 스냅샷해 그 위치에 배치.
  비워두면 메인 카메라 앞에 배치합니다.
- `MeshPresso > Voice To 3D > Setup Scene` 메뉴로 설정 에셋 생성 + 컨트롤러 배치.
- `MeshPresso > Voice To 3D > Repair Selected Model Textures` 메뉴로 텍스처가 빠진 기존 생성물 복구.
- glb(glTFast)와 fbx(내장 임포터) 모두 지원. 기본값은 glb.
- 플레이 모드 종료 후 배치된 오브젝트를 같은 위치에 자동 재배치.
