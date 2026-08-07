* ensure that nothing remains that identifies kubernetes nodes to VMs by role name, hostname, kubernetes node name, vm name or anything else. everything should be by a guid - except mayby hyper-v host hints from within the KVP since we aren't sure what's exposed there
* review all code for violations of the "fail closed" rule
* review all code for possibilities to minimize wmi, winrm, cross-host, and such calls
* review all code for possibilities to reduce "fan out" behavior - enumerating VMs, cluster nodes, Disks, anything in kubernetes
* review for correctness and remove sketch-design.md
* same for timeout cancelation status.md
* project name
* squash and move to new repo
* integration testing - there's a csi testing package apparently
* pivot to full copy ("copy" on refs) for snapshots and implement snapshots
* consider agent installation and distributed config instead of putting it on the CSV - mirror sql clustering and consider what's possible for leveraging the windows cluster apis - installer that registers with the cluster, saves it's config directly into cluster db, that would be cool
* full certificate pinning

failure scenario testing/to think about (some of these should be impossible if deployed according to the project requirements)
* volume attachment on a node that we can't find the VM for
* agent not responding
* agent cannot contact another node the cluster says is online
* brainstorm more with claude
