<!-- appsurface:unreleased-entry section="included" -->
### Reliable release preparation

- AppSurface release preparation now accepts an unchanged canonical Unreleased documentation sidecar while still
  rejecting added, deleted, or renamed next-cycle metadata. Maintainers can prepare the next coordinated release
  without manufacturing a no-op sidecar edit when its provisional metadata already matches the approved template.
