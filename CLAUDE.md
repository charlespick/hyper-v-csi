# Working conventions

- Until first release, don't open pull requests. Commit and merge changes locally
  (or push directly to the working branch) instead of going through GitHub PRs.
- Land each piece of work as a single squashed commit on the working branch — no
  merge commits, no feature branch left behind. If a change was developed on a
  scratch branch, squash it onto the working branch and delete the branch. History
  should read as a flat sequence of self-contained changes.
