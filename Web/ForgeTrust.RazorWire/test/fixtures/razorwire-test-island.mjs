export async function mount(root, props) {
  if (props.attributeName) {
    root.setAttribute(props.attributeName, String(props.attributeValue ?? ''));
  }

  if (props.recordChildCountAttribute) {
    root.setAttribute(props.recordChildCountAttribute, String(root.children.length));
  }
}
