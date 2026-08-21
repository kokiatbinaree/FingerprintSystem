(() => {
  const MAX_SCALE = 5;
  const MIN_SCALE = 1;
  const STEP = 1.18;

  function setup(box) {
    if (box.dataset.zoomEnhancer === '1') return;
    box.dataset.zoomEnhancer = '1';

    const layer = box.querySelector('.zoom-layer');
    if (!layer) return;

    let scale = 1;
    let panX = 0;
    let panY = 0;
    let dragging = false;
    let dragX = 0;
    let dragY = 0;
    let dragPanX = 0;
    let dragPanY = 0;

    const apply = (animate = true) => {
      layer.style.transform = `translate(calc(-50% + ${panX}px),calc(-50% + ${panY}px)) scale(${scale})`;
      layer.style.transition = animate && !dragging ? 'transform 140ms ease-out' : 'none';
      box.style.cursor = scale > 1 ? (dragging ? 'grabbing' : 'grab') : 'default';
    };

    const reset = () => {
      scale = 1;
      panX = 0;
      panY = 0;
      dragging = false;
      apply(false);
    };

    const wheel = (e) => {
      e.preventDefault();
      e.stopImmediatePropagation();

      const rect = box.getBoundingClientRect();
      const mx = e.clientX - rect.left - rect.width / 2;
      const my = e.clientY - rect.top - rect.height / 2;
      const next = Math.min(MAX_SCALE, Math.max(MIN_SCALE, scale * (e.deltaY < 0 ? STEP : 1 / STEP)));
      if (next === scale) return;

      // Image-space coordinate currently under the mouse.
      const worldX = (mx - panX) / scale;
      const worldY = (my - panY) / scale;

      // Move that coordinate toward the visual center while zooming.
      const targetX = mx * (scale / next);
      const targetY = my * (scale / next);
      panX = targetX - worldX * next;
      panY = targetY - worldY * next;
      scale = next;
      apply(true);
    };

    const pointerDown = (e) => {
      e.preventDefault();
      e.stopImmediatePropagation();
      if (scale <= 1 || e.button !== 0) return;
      box.setPointerCapture?.(e.pointerId);
      dragging = true;
      dragX = e.clientX;
      dragY = e.clientY;
      dragPanX = panX;
      dragPanY = panY;
      apply(false);
    };

    const pointerMove = (e) => {
      if (!dragging) return;
      e.preventDefault();
      e.stopImmediatePropagation();
      panX = dragPanX + (e.clientX - dragX);
      panY = dragPanY + (e.clientY - dragY);
      apply(false);
    };

    const pointerUp = (e) => {
      if (!dragging) return;
      e.preventDefault();
      e.stopImmediatePropagation();
      dragging = false;
      if (box.hasPointerCapture?.(e.pointerId)) box.releasePointerCapture(e.pointerId);
      apply(false);
    };

    const doubleClick = (e) => {
      e.preventDefault();
      e.stopImmediatePropagation();
      reset();
    };

    box.addEventListener('wheel', wheel, { capture: true, passive: false });
    box.addEventListener('pointerdown', pointerDown, { capture: true });
    box.addEventListener('pointermove', pointerMove, { capture: true });
    box.addEventListener('pointerup', pointerUp, { capture: true });
    box.addEventListener('pointercancel', pointerUp, { capture: true });
    box.addEventListener('dblclick', doubleClick, { capture: true });

    const observer = new MutationObserver(() => {
      // React changes the child node when switching between stored images/live preview.
      reset();
    });
    observer.observe(box, { childList: true, subtree: false });

    apply(false);
  }

  const scan = () => document.querySelectorAll('.zoom-box').forEach(setup);
  scan();
  new MutationObserver(scan).observe(document.body, { childList: true, subtree: true });
})();
