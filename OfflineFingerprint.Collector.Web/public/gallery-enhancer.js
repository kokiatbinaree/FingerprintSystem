(() => {
  const style = document.createElement('style');
  style.textContent = `
    .gallery-scroll { position: relative; scrollbar-width: thin; scrollbar-color: rgba(23,32,51,.28) transparent; }
    .gallery-scroll::-webkit-scrollbar { width: 7px; height: 7px; }
    .gallery-scroll::-webkit-scrollbar-track { background: transparent; }
    .gallery-scroll::-webkit-scrollbar-thumb { background: rgba(23,32,51,.25); border-radius: 999px; border: 2px solid transparent; background-clip: padding-box; }
    .gallery-item { position: relative; overflow: hidden; }
    .gallery-item img { width: 100% !important; height: 115px !important; object-fit: cover !important; object-position: center center !important; display: block; }
    .gallery-meta { display: block !important; padding: 0 2px 1px !important; }
    .gallery-meta b { position: absolute; left: 8px; top: 8px; z-index: 2; color: #fff !important; background: rgba(0,0,0,.58); border-radius: 5px; padding: 2px 5px; font-size: 10px !important; line-height: 1; text-shadow: 0 1px 2px #000; }
    .gallery-meta small { display: block !important; color: #6b7280 !important; font-size: 9px !important; white-space: nowrap; line-height: 1.25 !important; }
    .gallery-help { display: none !important; }
  `;
  document.head.appendChild(style);

  function formatDateTime(text) {
    const m = String(text || '').match(/(\d{1,2})\/(\d{1,2})\/(\d{4})(?:\s+|T)(\d{1,2}):(\d{2}):(\d{2})/);
    if (!m) return text;
    const dd = m[1].padStart(2, '0');
    const mm = m[2].padStart(2, '0');
    const yyyy = Number(m[3]);
    const yy = String((yyyy > 2400 ? yyyy - 543 : yyyy) % 100).padStart(2, '0');
    const hh = m[4].padStart(2, '0');
    return `${hh}:${m[6]}:${m[6] ? m[5] : '00'} ${dd}/${mm}/${yy}`;
  }

  function refine() {
    document.querySelectorAll('.gallery-item').forEach(item => {
      const small = item.querySelector('.gallery-meta small');
      if (small) {
        const original = small.getAttribute('data-format-source') || small.textContent || '';
        if (!small.getAttribute('data-format-source')) small.setAttribute('data-format-source', original);
        const m = String(original).match(/(\d{1,2})\/(\d{1,2})\/(\d{4})(?:\s+|T)(\d{1,2}):(\d{2}):(\d{2})/);
        if (m) {
          const dd = m[1].padStart(2, '0');
          const mm = m[2].padStart(2, '0');
          const yyyy = Number(m[3]);
          const yy = String((yyyy > 2400 ? yyyy - 543 : yyyy) % 100).padStart(2, '0');
          const hh = m[4].padStart(2, '0');
          small.textContent = `${hh}:${m[5]}:${m[6]} ${dd}/${mm}/${yy}`;
        }
      }
    });
  }

  refine();
  new MutationObserver(refine).observe(document.body, { childList: true, subtree: true });
})();
