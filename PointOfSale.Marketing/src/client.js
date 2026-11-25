// Progressive enhancement only. No router, React runtime, storage, fetch or telemetry.
const menus = () => [...document.querySelectorAll('details[data-menu]')];
function closeMenu(menu, restoreFocus = false) {
  menu.open = false;
  if (restoreFocus) menu.querySelector('summary').focus();
}
document.addEventListener('keydown', event => {
  if (event.key === 'Escape') {
    const open = menus().find(menu => menu.open);
    if (open) { event.preventDefault(); closeMenu(open, true); }
  }
});
for (const eventName of ['click', 'focusin']) document.addEventListener(eventName, event => {
  for (const menu of menus()) if (menu.open && !menu.contains(event.target)) closeMenu(menu);
});
document.addEventListener('click', event => {
  const anchor = event.target.closest('a[href^="#"]');
  if (!anchor) return;
  const target = document.getElementById(anchor.hash.slice(1));
  if (!target) return;
  for (const menu of menus()) closeMenu(menu);
  // Do not cancel native hash navigation/history.
  target.setAttribute('tabindex', '-1');
  target.focus({ preventScroll: true });
});
function focusDestination() {
  if (!location.hash) return;
  let id;
  try { id = decodeURIComponent(location.hash.slice(1)); } catch { return; }
  const target = document.getElementById(id);
  if (target) {
    if (!target.matches('a,button,input,summary,[tabindex]')) target.setAttribute('tabindex', '-1');
    target.focus({ preventScroll: true });
  }
}
// Native fragment navigation and the click handler already focus same-document
// targets. Do not refocus asynchronously on hashchange: that can steal the next
// keyboard action from a user who has already moved to another control.
focusDestination();
