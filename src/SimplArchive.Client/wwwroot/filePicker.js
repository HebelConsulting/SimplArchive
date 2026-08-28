// Opens the file dialog of a hidden <input type="file"> from a REAL button's click handler (#511).
// A <label for=…> over a display:none input is unreachable by keyboard — the label is not focusable and the
// hidden input is out of the tab order — and has no button role for assistive technology. A genuine <button>
// forwarding its activation keeps focus, Enter/Space and the role native; this one line is all the bespoke
// behaviour that approach needs.
window.openFilePicker = (id) => document.getElementById(id)?.click();
