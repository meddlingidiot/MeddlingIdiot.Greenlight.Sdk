# Changelog

All notable changes to this project will be documented in this file.

## [Unreleased] - 1.2.0

### Added

- `hold_indicators` command, and `HoldIndicatorsAsync` / `ReleaseIndicatorsAsync` on the
  client: hold every indicator Greenlight drives at a colour and/or a pretend build, the way
  its developer page does — for showing a client off or checking it responds without waiting
  for a real build to break. Gated by the same *allow commands* setting as the rest; the host
  releases the hold when the client that placed it detaches. Protocol version stays 1: the
  command is additive, and a host that predates it answers `unknown_command`.

## 1.1.0

