(() => {
  function guard(e) {
    const cell = e.target.closest?.('.finger-cell');
    if (!cell) return;
    const row = cell.closest('.finger-table tbody tr');
    if (!row || !row.classList.contains('finger-row-active')) {
      e.preventDefault();
      e.stopImmediatePropagation();
    }
  }

  document.addEventListener('click', guard, true);
})();
