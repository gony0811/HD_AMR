// 학습 로그용 "바닥에 붙어 있을 때만 자동 스크롤" 헬퍼.
// 텍스트 갱신 직전에 스크롤이 바닥 근처인지 판정하고, 바닥일 때만 갱신 후 맨 아래로 따라간다.
// 사용자가 위로 올려 이력을 보는 중이면(바닥 아님) 새 로그가 쌓여도 위치를 고정한다.
window.hdAmrLog = {
    set: function (el, text) {
        if (!el) return;
        var atBottom = (el.scrollHeight - el.scrollTop - el.clientHeight) < 30;
        if (el.textContent !== text) el.textContent = text;
        if (atBottom) el.scrollTop = el.scrollHeight;
    }
};
