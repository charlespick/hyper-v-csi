* ensure that nothing remains that identifies kubernetes nodes to VMs by role name, hostname, kubernetes node name, vm name or anything else. everything should be by a guid - except mayby hyper-v host hints from within the KVP since we aren't sure what's exposed there
* review all code for violations of the "fail closed" rule
* review all code for possibilities to minimize wmi, winrm, cross-host, and such calls
* review all code for possibilities to reduce "fan out" behavior - enumerating VMs, cluster nodes, Disks, anything in kubernetes
* review for correctness and remove sketch-design.md
* same for timeout cancelation status.md
* project name
* squash and move to new repo
* integration testing - there's a csi testing package apparently
