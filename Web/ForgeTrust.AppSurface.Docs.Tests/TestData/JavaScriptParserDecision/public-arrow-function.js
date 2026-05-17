/**
 * Creates a listener that can observe RazorWire form failures.
 * @public
 */
const createFailureListener = (callback) => {
  return (event) => callback(event.detail);
};
