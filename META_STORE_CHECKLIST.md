# RegionalAR — Meta Quest Store Alpha Submission Checklist

## Developer Account
- [ ] Create Meta Quest Developer account at developer.meta.com
- [ ] Complete organization verification

## Store Listing Assets
- [ ] **App Icon:** 1024×1024 px, PNG, no transparency
- [ ] **Cover Art:** 3840×2160 px (16:9), JPEG or PNG
- [ ] **Hero Image:** 4096×1024 px (for featured placements)
- [ ] **Screenshots:** minimum 3, recommended 5+ — 2560×1440 px (16:9), PNG or JPEG
- [ ] **Trailer Video:** recommended, 1080p+, 30–60 seconds, MP4

## Metadata
- [ ] App name (max ~50 characters): "RegionalAR"
- [ ] Short description (~160 chars)
- [ ] Long description (~4000 chars)
- [ ] Category: Education or Health
- [ ] Comfort rating: Comfortable (stationary AR, no locomotion)
- [ ] Supported languages
- [ ] Release notes for v1.0

## Age Rating
- [ ] Complete IARC rating questionnaire in the Developer Dashboard

## Privacy Policy
- [ ] Create and host a publicly accessible privacy policy URL
- [ ] Disclose: no user data collection, no analytics, no network use beyond local WiFi transfer
- [ ] Add URL to the Developer Dashboard

## Technical / APK Requirements
- [ ] Target: ARM64 (arm64-v8a)
- [ ] Minimum SDK: API 29 (Android 10)
- [ ] Target SDK: API 32+ (Android 12L) — verify current Meta minimum
- [ ] AndroidManifest includes `com.oculus.intent.category.VR` intent filter
- [ ] Headset compatibility entries: Quest 2, Quest 3, Quest Pro
- [ ] APK signed with release key (not debug)
- [ ] Sustained 72 Hz framerate (test with OVR Metrics Tool)
- [ ] No crashes on cold start, volume load, or panel interaction

## Medical/Educational Notes
- [ ] Do NOT claim FDA clearance or medical device status
- [ ] Frame as educational/training tool for Regional Anesthesia
- [ ] No diagnostic claims in description or screenshots

## Pre-Submission Testing
- [ ] Fresh install: bundled sample appears in library on first launch
- [ ] Load sample volume from library — renders correctly
- [ ] WiFi download from desktop app works
- [ ] All control panel buttons functional (layers, opacity, cut planes, markers)
- [ ] Grab, scale, rotate volume works
- [ ] No leftover debug text visible to user
- [ ] Audio feedback (bong) plays on button press

## Submission Steps
1. Create app in Developer Dashboard, select Quest platform
2. Upload signed APK
3. Fill all metadata fields + upload assets
4. Complete IARC rating questionnaire
5. Submit for App Review (typically 5–15 business days)
6. On approval, set distribution (public or unlisted) and publish
