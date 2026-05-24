# ForgeTrust.RazorWire

RazorWire is the AppSurface package for server-rendered interaction loops: register the package services, map the stream endpoint, configure package options, and return stream updates from MVC controllers without replacing the rest of ASP.NET Core.

Use this namespace page as the API starting point when you already know you are integrating RazorWire and need the public registration, endpoint, and options types in one place.

Use `RazorWireStreamBuilder.Visit(...)` when an active RazorWire stream should trigger a same-origin Turbo Drive navigation. It emits the `rw-visit` stream command and supports `RazorWireVisitAction.Advance` or `RazorWireVisitAction.Replace`. Keep visit commands out of retained replay channels; replay is for idempotent state snapshots, while navigation is a one-shot browser command.
