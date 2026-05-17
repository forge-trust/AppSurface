/**
 * Public browser global used by advanced RazorWire consumers.
 * @public
 * @global
 */
window.RazorWire = {
  version: "0.1.0",
  refresh(target) {
    target.dispatchEvent(new CustomEvent("razorwire:refresh"));
  }
};
