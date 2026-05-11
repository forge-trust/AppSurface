// This is a JavaScript module that is loaded on demand. It can export any number of functions.
/**
 * Show a browser prompt initialized with the given message and a default input string.
 * @param {string} message - The message displayed in the prompt dialog.
 * @returns {string|null} The text entered by the user, or `null` if the dialog was dismissed.
 */

export function showPrompt(message) {
  return prompt(message, 'Type anything here');
}