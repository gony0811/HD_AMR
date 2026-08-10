/*
 * 젯슨 모니터링 화면의 동작.
 *
 * 폴링 구조다. 이미지가 어차피 별도 HTTP 리소스라 요청은 나가게 되어 있고, 관찰자가
 * 브라우저 한둘뿐이라 WebSocket 을 얹어도 얻는 게 없다. 대신 재연결·백프레셔 처리를
 * 떠안게 되는데, 현장에서 화면이 멈췄을 때 원인이 하나 더 늘어나는 셈이라 피했다.
 *
 * 이미지 URL 에 seq 를 붙이는 것이 핵심이다. 서버가 게시한 프레임 번호를 그대로 쓰므로
 * (1) 브라우저 캐시가 정상 동작하고 (2) 거리·진폭·오버레이 세 장이 항상 같은 프레임이 된다.
 */

const R = {
    preview: '/api/lidar/preview',
    previewOptions: '/api/lidar/preview/options',
    detector: '/api/lidar/detector',
    status: '/api/lidar/status',
    config: '/api/lidar/config',
    persist: '/api/lidar/config/persist',
    measure: '/api/lidar/measure',
};

const $ = (id) => document.getElementById(id);

const el = {
    banner: $('banner'),
    pillConnection: $('pill-connection'),
    pillLink: $('pill-link'),
    model: $('stat-model'),
    temp: $('stat-temp'),
    voltage: $('stat-voltage'),
    fps: $('stat-fps'),
    rangeLegend: $('range-legend'),
    amplitudeLegend: $('amplitude-legend'),
    distance: $('img-distance'),
    amplitude: $('img-amplitude'),
    overlayA: $('img-overlay-a'),
    overlayB: $('img-overlay-b'),
    frameStats: $('frame-stats'),
    detectVerdict: $('detect-verdict'),
    detectStats: $('detect-stats'),
    measureResult: $('measure-result'),
};

let lastSeq = -1;

// ── 공용 ────────────────────────────────────────────────────────────────────

async function api(url, options) {
    const response = await fetch(url, options);
    if (!response.ok) {
        const text = await response.text().catch(() => '');
        throw new Error(`${response.status} ${response.statusText}${text ? ` — ${text}` : ''}`);
    }
    return response.status === 204 ? null : response.json();
}

const patch = (url, body) => api(url, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
});

function banner(message) {
    el.banner.textContent = message || '';
    el.banner.hidden = !message;
}

const num = (id) => {
    const value = parseFloat($(id).value);
    return Number.isFinite(value) ? value : undefined;
};

const fmt = (value, digits = 1) =>
    value === null || value === undefined ? '—' : Number(value).toFixed(digits);

function rows(target, entries) {
    target.innerHTML = '';
    for (const [label, value, tone] of entries) {
        const row = document.createElement('div');
        const key = document.createElement('span');
        const val = document.createElement('b');
        key.textContent = label;
        val.textContent = value;
        if (tone) val.className = tone;
        row.append(key, val);
        target.append(row);
    }
}

// ── 상태 ────────────────────────────────────────────────────────────────────

async function pollStatus() {
    try {
        const s = await api(R.status);

        el.pillConnection.textContent = s.connected ? '센서 연결됨' : '센서 끊김';
        el.pillConnection.className = `pill ${s.connected ? 'ok' : 'bad'}`;

        // linkUp 이 null 인 것은 "링크가 없다"가 아니라 "확인할 수 없다"다(USB 구성이거나
        // 인터페이스명이 설정되지 않은 경우). 이 둘을 같게 표시하면 멀쩡한 장비를 두고
        // 케이블부터 뽑아보게 된다.
        if (s.linkUp === null || s.linkUp === undefined) {
            el.pillLink.textContent = '링크 확인 불가';
            el.pillLink.className = 'pill';
        } else {
            el.pillLink.textContent = s.linkUp ? '링크 정상' : '링크 없음 — 케이블 확인';
            el.pillLink.className = `pill ${s.linkUp ? 'ok' : 'bad'}`;
        }

        el.model.textContent = s.model || '—';
        el.temp.textContent = s.sensorTemperatureC ? `${fmt(s.sensorTemperatureC)} ℃` : '—';
        el.voltage.textContent = s.voltageV ? `${fmt(s.voltageV, 2)} V` : '—';

        banner(s.lastError && !s.connected ? s.lastError : '');
    } catch (error) {
        el.pillConnection.textContent = '서비스 응답 없음';
        el.pillConnection.className = 'pill bad';
        banner(`상태 조회 실패: ${error.message}`);
    }
}

// ── 미리보기 ────────────────────────────────────────────────────────────────

async function pollPreview() {
    let info;
    try {
        info = await api(R.preview);
    } catch {
        // 503 은 아직 첫 프레임이 없다는 뜻이다. 루프는 이 요청 자체를 신호로 깨어나므로
        // 잠시 뒤 자연히 채워진다. 오류로 취급하지 않는다.
        el.fps.textContent = '대기 중';
        return;
    }

    if (info.seq !== lastSeq) {
        lastSeq = info.seq;
        el.distance.src = `${R.preview}/distance.png?s=${info.seq}`;
        el.amplitude.src = `${R.preview}/amplitude.png?s=${info.seq}`;

        setOverlay(info.detectionEnabled ? `${R.preview}/overlay.png?s=${info.seq}` : null);
    }

    el.fps.textContent = `${fmt(info.loopFps, 1)} fps`;
    el.rangeLegend.textContent = `${fmt(info.scale.minRangeMm, 0)} – ${fmt(info.scale.maxRangeMm, 0)} mm`;
    el.amplitudeLegend.textContent = `0 – ${info.scale.amplitudeMax}`;

    renderFrameStats(info);
    renderDetection(info);
}

function renderFrameStats(info) {
    const p = info.pixels;
    const pct = (n) => `${n.toLocaleString()} (${(n * 100 / Math.max(1, p.total)).toFixed(1)}%)`;

    // 포화와 ADC 오버플로는 노출을 내리라는 신호이고, 진폭 부족은 올리라는 신호다.
    // 어느 쪽이 지배적인지 색으로 바로 보이게 한다.
    const tone = (n, warnRatio) => {
        const ratio = n / Math.max(1, p.total);
        if (ratio >= warnRatio * 2) return 'bad';
        if (ratio >= warnRatio) return 'warn';
        return '';
    };

    rows(el.frameStats, [
        ['해상도', `${info.width} × ${info.height}`],
        ['프레임 번호', info.seq.toLocaleString()],
        ['센서 온도', `${fmt(info.temperatureC)} ℃`],
        ['완전 프레임', info.complete ? '예' : '아니오 — 부분 프레임', info.complete ? 'ok' : 'bad'],
        ['유효 픽셀', pct(p.valid), p.valid / Math.max(1, p.total) > 0.5 ? 'ok' : 'warn'],
        ['진폭 부족', pct(p.lowAmplitude), tone(p.lowAmplitude, 0.15)],
        ['포화', pct(p.saturation), tone(p.saturation, 0.05)],
        ['ADC 오버플로', pct(p.adcOverflow), tone(p.adcOverflow, 0.05)],
        ['미충전', pct(p.unfilled), p.unfilled > 0 ? 'bad' : ''],
        ['거리 최소', `${fmt(info.distanceMinMm, 0)} mm`],
        ['거리 중앙', `${fmt(info.distanceMedianMm, 0)} mm`],
        ['거리 최대', `${fmt(info.distanceMaxMm, 0)} mm`],
        ['진폭 P99', info.amplitudeP99 ?? '—'],
    ]);
}

function renderDetection(info) {
    if (!info.detectionEnabled) {
        el.detectVerdict.textContent = '검출이 꺼져 있다.';
        el.detectVerdict.className = 'verdict';
        rows(el.detectStats, []);
        return;
    }

    if (info.detectionSucceeded && info.ridge) {
        const r = info.ridge;
        el.detectVerdict.textContent = `능선 검출됨 — 신뢰도 ${fmt(info.confidence, 3)}`;
        el.detectVerdict.className = 'verdict ok';

        rows(el.detectStats, [
            ['중점 X', `${fmt(r.point.x)} mm`],
            ['중점 Y', `${fmt(r.point.y)} mm`],
            ['중점 Z', `${fmt(r.point.z)} mm`],
            ['방향', `(${fmt(r.direction.x, 3)}, ${fmt(r.direction.y, 3)}, ${fmt(r.direction.z, 3)})`],
            ['길이', `${fmt(r.lengthMm, 0)} mm`],
            ['피팅 RMS', `${fmt(r.rmsMm, 2)} mm`, r.rmsMm < 8 ? 'ok' : 'warn'],
            ...shapeRows(info),
            ['후보 점', info.candidateCount.toLocaleString()],
            ['검출 소요', `${fmt(info.detectMs, 0)} ms`],
        ]);
        return;
    }

    el.detectVerdict.textContent = info.failureDetail || '검출 실패.';
    el.detectVerdict.className = 'verdict bad';

    // 실패했을 때도 어디까지 갔는지 보여준다. 후보 점이 0 인 것과, 평판은 찾았는데
    // 비드를 못 찾은 것은 대응이 전혀 다르다.
    rows(el.detectStats, [
        ['실패 사유', info.failure || '—'],
        ['후보 점', info.candidateCount.toLocaleString()],
        ...shapeRows(info),
        ['검출 소요', `${fmt(info.detectMs, 0)} ms`],
    ]);
}

/**
 * 검출 방식에 따라 다른 진단값을 낸다.
 *
 * 반원 비드에서 가장 중요한 건 추정 반경이다. 배경이나 엉뚱한 곡면을 잡으면 반경이
 * 실물과 전혀 다르게 나오는데, 인라이어 수나 잔차는 그때도 정상으로 보인다.
 */
/**
 * 가로 위치 분포를 막대로 그린다. 무리 분리의 입력이라, 봉우리가 몇 개인지·골이 얼마나
 * 깊은지를 숫자로 추측하지 않고 바로 볼 수 있다. 무리에 들어간 칸은 색으로 구분해서
 * "분리가 분포와 맞게 됐는가"를 한눈에 대조한다.
 */
function renderHistogram(hist, clusters) {
    const box = $('histogram');
    if (!hist || !hist.counts || !hist.counts.length) {
        box.hidden = true;
        return;
    }

    box.hidden = false;

    const peak = Math.max(...hist.counts, 1);
    const ranges = (clusters || []).map(c => [c.centerMm - c.widthMm / 2, c.centerMm + c.widthMm / 2]);

    box.innerHTML = '';
    hist.counts.forEach((n, k) => {
        const mm = hist.startMm + (k + 0.5) * hist.binWidthMm;
        const bar = document.createElement('i');
        bar.style.height = `${Math.max(1, n * 100 / peak)}%`;
        if (ranges.some(([lo, hi]) => mm >= lo && mm <= hi)) bar.className = 'in';
        bar.title = `${mm.toFixed(0)} mm — ${n} 점`;
        box.append(bar);
    });

    $('histogram-legend').textContent =
        `${hist.startMm.toFixed(0)} ~ ${(hist.startMm + hist.counts.length * hist.binWidthMm).toFixed(0)} mm, ` +
        `칸 ${hist.binWidthMm.toFixed(0)}mm, 최대 ${peak}점`;
}

function shapeRows(info) {
    if (!info.arc) {
        return [
            ['평면 사잇각', info.planeAngleDeg ? `${fmt(info.planeAngleDeg, 1)}°` : '—'],
            ['평면 A 인라이어', info.planeAInlierCount.toLocaleString()],
            ['평면 B 인라이어', info.planeBInlierCount.toLocaleString()],
        ];
    }

    const a = info.arc;
    const expected = 35;   // 실물 반경. 화면에서는 참고용 색 판정에만 쓴다.
    const radiusOff = a.radiusMm ? Math.abs(a.radiusMm - expected) / expected : 1;

    renderHistogram(a.histogram, a.clusters);

    // 후보가 여럿이면 목표 위치를 주지 않는 한 프레임마다 다른 것이 선택될 수 있다.
    // 이게 실측에서 재현성을 무너뜨린 원인이라 눈에 띄게 표시한다.
    const candidates = info.candidateRidgeCount || 0;

    return [
        ['코러게이션 후보', candidates ? `${candidates}개` : '—',
            candidates === 1 ? 'ok' : candidates > 1 ? 'warn' : ''],
        ['추정 반경', a.radiusMm ? `${fmt(a.radiusMm, 1)} mm` : '—',
            a.radiusMm ? (radiusOff < 0.2 ? 'ok' : 'warn') : ''],
        ['원 중심 높이', a.radiusMm ? `${fmt(a.centerHeightMm, 1)} mm` : '—',
            a.radiusMm && Math.abs(a.centerHeightMm) < 10 ? 'ok' : 'warn'],
        ['원 피팅 RMS', a.circleRmsMm ? `${fmt(a.circleRmsMm, 2)} mm` : '—'],
        ['평판 RMS', `${fmt(a.planeRmsMm, 2)} mm`],
        ['평판 인라이어', info.planeAInlierCount.toLocaleString()],
        ['코러게이션 점', a.corrugationPointCount.toLocaleString(),
            a.corrugationPointCount > 300 ? 'ok' : 'warn'],
        ['후보 인라이어 합', info.planeBInlierCount.toLocaleString()],
        ['최대 높이', `${fmt(a.maxHeightMm, 1)} mm`],
    ];
}

// ── 설정 폼 ─────────────────────────────────────────────────────────────────

/**
 * 오버레이 이미지 갱신. 검출이 꺼져 있으면 src 를 비우는 대신 <b>속성을 제거</b>한다 —
 * src="" 는 브라우저에 따라 현재 문서 URL 을 다시 요청하는 것으로 해석된다.
 */
function setOverlay(src) {
    for (const img of [el.overlayA, el.overlayB]) {
        if (src) img.src = src;
        else img.removeAttribute('src');
    }
    applyOverlayVisibility();
}

function applyOverlayVisibility() {
    const visible = $('opt-overlay').checked && el.overlayA.hasAttribute('src');
    el.overlayA.style.display = visible ? '' : 'none';
    el.overlayB.style.display = visible ? '' : 'none';
}

async function loadPreviewOptions() {
    const o = await api(R.previewOptions);
    $('opt-detection').checked = o.detectionEnabled;
    $('opt-follow').checked = o.followDetectorBand;
    $('opt-interval').value = o.intervalMs;
    $('opt-minrange').value = o.minRangeMm;
    $('opt-maxrange').value = o.maxRangeMm;
    $('opt-amplitude').value = o.amplitudeMax;
    $('opt-band').value = o.ridgeBandMm;
}

async function savePreviewOptions() {
    await patch(R.previewOptions, {
        detectionEnabled: $('opt-detection').checked,
        followDetectorBand: $('opt-follow').checked,
        intervalMs: num('opt-interval'),
        minRangeMm: num('opt-minrange'),
        maxRangeMm: num('opt-maxrange'),
        amplitudeMax: num('opt-amplitude'),
        ridgeBandMm: num('opt-band'),
    });
    await loadPreviewOptions();
}

async function loadDetectorOptions() {
    const o = await api(R.detector);
    $('det-mindist').value = o.minDistanceMm;
    $('det-maxdist').value = o.maxDistanceMm;
    $('det-inlier').value = o.inlierThresholdMm;
    $('det-mininliers').value = o.minPlaneInliers;
    $('det-minangle').value = o.minPlaneAngleDeg;
    $('det-maxrms').value = o.maxRmsMm;
    $('det-minlength').value = o.minRidgeLengthMm;
    $('det-iterations').value = o.ransacIterations;
}

async function saveDetectorOptions() {
    await patch(R.detector, {
        minDistanceMm: num('det-mindist'),
        maxDistanceMm: num('det-maxdist'),
        inlierThresholdMm: num('det-inlier'),
        minPlaneInliers: num('det-mininliers'),
        minPlaneAngleDeg: num('det-minangle'),
        maxRmsMm: num('det-maxrms'),
        minRidgeLengthMm: num('det-minlength'),
        ransacIterations: num('det-iterations'),
    });
    await loadDetectorOptions();
}

async function loadSensorConfig() {
    const c = await api(R.config);
    $('cfg-integration').value = c.integrationTime3D;
    $('cfg-minamp').value = c.minAmplitude;
    $('cfg-hdr').value = c.hdr;
    $('cfg-modulation').value = c.modulation;
    $('cfg-median').checked = c.medianFilter;
    $('cfg-average').checked = c.averageFilter;

    if (c.roi) {
        $('cfg-roi-xmin').value = c.roi.xMin;
        $('cfg-roi-xmax').value = c.roi.xMax;
        $('cfg-roi-ymin').value = c.roi.yMin;
        $('cfg-roi-ymax').value = c.roi.yMax;
    }
}

async function saveSensorConfig() {
    await patch(R.config, {
        integrationTime3D: num('cfg-integration'),
        minAmplitude: num('cfg-minamp'),
        hdr: $('cfg-hdr').value,
        modulation: $('cfg-modulation').value,
        medianFilter: $('cfg-median').checked,
        averageFilter: $('cfg-average').checked,
        roi: {
            xMin: num('cfg-roi-xmin'),
            yMin: num('cfg-roi-ymin'),
            xMax: num('cfg-roi-xmax'),
            yMax: num('cfg-roi-ymax'),
        },
    });
    await loadSensorConfig();
}

// ── 버튼 배선 ───────────────────────────────────────────────────────────────

/** 눌린 동안 버튼을 잠근다. 설정 변경은 네이티브 스레드를 거치므로 연타하면 큐가 밀린다. */
function wire(id, action, busyLabel) {
    const button = $(id);
    const original = button.textContent;

    button.addEventListener('click', async () => {
        button.disabled = true;
        button.textContent = busyLabel;
        banner('');

        try {
            await action();
        } catch (error) {
            banner(`${original} 실패: ${error.message}`);
        } finally {
            button.disabled = false;
            button.textContent = original;
        }
    });
}

wire('btn-preview-apply', savePreviewOptions, '적용 중…');
wire('btn-detector-apply', saveDetectorOptions, '적용 중…');
wire('btn-config-apply', saveSensorConfig, '적용 중…');

wire('btn-config-persist', async () => {
    // 되돌릴 수 없는 쓰기다. EEPROM 수명도 유한하므로 실수로 눌리지 않게 한 번 막는다.
    if (!confirm('현재 설정을 장비 EEPROM 에 영구 기록한다. 되돌릴 수 없다. 계속할까?')) return;
    await api(R.persist, { method: 'POST' });
    banner('장비에 영구 저장했다.');
}, '저장 중…');

wire('btn-measure', async () => {
    const result = await api(R.measure, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ frames: num('measure-frames') ?? 10 }),
    });

    el.measureResult.hidden = false;
    el.measureResult.textContent = JSON.stringify(result, null, 2);
}, '측정 중…');

$('opt-overlay').addEventListener('change', applyOverlayVisibility);

// ── 시작 ────────────────────────────────────────────────────────────────────

async function start() {
    applyOverlayVisibility();

    // 센서가 아직 안 붙었어도 화면은 떠야 한다. 설정 조회는 핸들이 열려 있어야
    // 성공하므로 실패를 개별적으로 흡수하고, 워치독이 붙인 뒤 다시 시도한다.
    await Promise.allSettled([loadPreviewOptions(), loadDetectorOptions(), loadSensorConfig()]);

    await pollStatus();
    setInterval(pollStatus, 2000);

    // 미리보기 폴링은 서버 갱신 주기와 무관하게 고정 간격으로 돈다. seq 가 그대로면
    // 이미지 요청 자체가 나가지 않으므로(URL 이 동일해 캐시 적중) 낭비가 없다.
    setInterval(pollPreview, 300);
    await pollPreview();
}

start();
