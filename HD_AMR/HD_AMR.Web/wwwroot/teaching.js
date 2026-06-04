window.hdAmrTeaching = {
    // Convert a (clientX, clientY) pair from a mouse event into the SVG's user-coordinate space
    // by inverting the screen CTM. Returns [x, y].
    svgPoint: function (svgEl, clientX, clientY) {
        if (!svgEl || typeof svgEl.createSVGPoint !== "function") return [0, 0];
        var pt = svgEl.createSVGPoint();
        pt.x = clientX;
        pt.y = clientY;
        var ctm = svgEl.getScreenCTM();
        if (!ctm) return [0, 0];
        var inv = ctm.inverse();
        var out = pt.matrixTransform(inv);
        return [out.x, out.y];
    },

    // Zoom the drawing with the mouse wheel ONLY while the cursor is over the SVG. The listener
    // is bound to the SVG element, so it never fires when the cursor is elsewhere on the page —
    // there the wheel scrolls the page normally. A native { passive: false } listener is required
    // so preventDefault() actually stops the page from scrolling while we zoom (Blazor's own wheel
    // listener is passive and cannot). The SVG-space anchor under the cursor and the scroll delta
    // are passed to .NET (ZoomFromJs). deltaY carries the wheel; fall back to deltaX otherwise.
    // Idempotent per element.
    attachWheelZoom: function (svgEl, dotnetRef) {
        if (!svgEl || svgEl._hdWheelHandler) return;
        var handler = function (e) {
            e.preventDefault();
            var pt = window.hdAmrTeaching.svgPoint(svgEl, e.clientX, e.clientY);
            var delta = e.deltaY !== 0 ? e.deltaY : e.deltaX;
            dotnetRef.invokeMethodAsync("ZoomFromJs", pt[0], pt[1], delta);
        };
        svgEl.addEventListener("wheel", handler, { passive: false });
        svgEl._hdWheelHandler = handler;
    },

    // How many viewBox units (mm) does one screen pixel correspond to?
    // Computed via the inverse screen CTM, which already accounts for letterboxing
    // from preserveAspectRatio="xMidYMid meet".
    mmPerPixel: function (svgEl) {
        if (!svgEl || typeof svgEl.createSVGPoint !== "function") return 0;
        var ctm = svgEl.getScreenCTM();
        if (!ctm) return 0;
        var inv = ctm.inverse();
        var p0 = svgEl.createSVGPoint(); p0.x = 0; p0.y = 0;
        var p1 = svgEl.createSVGPoint(); p1.x = 1; p1.y = 0;
        var w0 = p0.matrixTransform(inv);
        var w1 = p1.matrixTransform(inv);
        return Math.hypot(w1.x - w0.x, w1.y - w0.y);
    }
};
