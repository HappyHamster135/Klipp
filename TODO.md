# Klipp — TODO innan release

## Preflight checks vid app-uppstart

Appen måste verifiera att alla nödvändiga komponenter finns på systemet
INNAN användaren försöker spela in. Visa tydliga felmeddelanden med länkar
till hur problemet löses.

### Krav att verifiera

- [ ] **Media Foundation H.264 encoder finns**
  - Kör `EncoderDiscovery.ListH264Encoders()` vid startup
  - Om tom: visa modal "Klipp kräver Media Feature Pack"
  - Länk till: https://support.microsoft.com/en-us/topic/media-feature-pack-list-for-windows-n-editions-c1c6fffa-d052-8338-7a79-a4bb980a700a
  - Vissa Windows 11 Pro-installationer (även icke-N) saknar encoders
    av OEM-skäl — Media Feature Pack från Optional Features löser det.

- [ ] **Windows Graphics Capture stöds**
  - Kontrollera `GraphicsCaptureSession.IsSupported()`
  - Kräver Windows 10 1903+ (build 18362)
  - Om false: visa "Klipp kräver Windows 10 May 2019 Update eller senare"

- [ ] **DirectX 11.1+ tillgängligt**
  - Skapa en test-D3D11Device vid startup
  - Om misslyckas: visa "Klipp kräver en grafikkort med DirectX 11.1-stöd"

- [ ] **Diskutrymme för clips**
  - Kontrollera ledigt utrymme i clips-mappen
  - Varna om < 5 GB ledigt

- [ ] **WASAPI loopback-audio fungerar**
  - Test-init av audio capture
  - Om fail: varna "Ljud-inspelning otillgänglig" men låt appen köra
