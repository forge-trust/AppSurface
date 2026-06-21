export function mount(root, props) {
  root.textContent = `client:${props.label}`;
  root.dataset.clientMounted = 'true';
}
