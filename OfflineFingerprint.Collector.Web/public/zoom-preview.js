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
      layer.style.transition = animate && !dragging ? 'transform 120ms ease-out' : 'none';
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
      const mouseX = e.clientX - rect.left - rect.width / 2;
      const mouseY = e.clientY - rect.top - rect.height / 2;
      const next = Math.min(MAX_SCALE, Math.max(MIN_SCALE, scale * (e.deltaY < 0 ? STEP : 1 / STEP)));
      if (next === scale) return;
      const imageX = (mouseX - panX) / scale;
      const imageY = (mouseY - panY) / scale;
      panX = mouseX - imageX * next;
      panY = mouseY - imageY * next;
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
    const observer = new MutationObserver(() => reset());
    observer.observe(box, { childList: true, subtree: false });
    apply(false);
  }

  function setupUiEnhancements() {
    const styleId = 'fp-ui-enhancer-style';
    if (!document.getElementById(styleId)) {
      const style = document.createElement('style');
      style.id = styleId;
      style.textContent = `
        .collection-message{min-width:220px!important;max-width:360px!important;height:38px!important;font-size:14px!important}
        .finger-cell.has-images.fp-save-pulse{animation:fp-save-cell .48s ease-out}
        @keyframes fp-save-cell{0%{transform:scale(.94)}55%{transform:scale(1.05)}100%{transform:scale(1)}}
        .gallery-item.fp-new-save{animation:fp-save-gallery .5s cubic-bezier(.2,.8,.2,1)}
        @keyframes fp-save-gallery{0%{opacity:0;transform:translateY(-18px) scale(.97)}65%{opacity:1;transform:translateY(2px) scale(1.01)}100%{opacity:1;transform:translateY(0) scale(1)}}
      `;
      document.head.appendChild(style);
    }
    document.querySelectorAll('.finger-cell').forEach(btn => {
      if (btn.dataset.fpTableEnhance === '1') return;
      btn.dataset.fpTableEnhance = '1';
      btn.addEventListener('click', e => {
        const scannerIsOn = !!document.querySelector('.scanner-toggle.on');
        if (!scannerIsOn) {
          e.preventDefault();
          e.stopImmediatePropagation();
          btn.classList.add('fp-disabled-cell');
          window.setTimeout(() => btn.classList.remove('fp-disabled-cell'), 220);
        }
      }, {capture:true});
    });
    document.querySelectorAll('.gallery-scroll').forEach(scroll => {
      const items = Array.from(scroll.querySelectorAll('.gallery-item'));
      const signature = items.map(x => x.textContent || '').join('|');
      if (scroll.dataset.fpGallerySignature === signature) return;
      scroll.dataset.fpGallerySignature = signature;
      if (items.length > 0) {
        const fresh = items[0];
        fresh.classList.add('fp-new-save');
        window.setTimeout(() => fresh.classList.remove('fp-new-save'), 650);
      }
    });
  }

  const scan = () => {
    document.querySelectorAll('.zoom-box').forEach(setup);
    setupUiEnhancements();
    if(!window.__fingerprintSingleDeleteLoader){window.__fingerprintSingleDeleteLoader=true;const s=document.createElement('script');s.src='/fingerprint-single-delete.js';s.defer=true;document.head.appendChild(s);}
  };
  scan();
  new MutationObserver(scan).observe(document.body, {childList:true,subtree:true});
})();
