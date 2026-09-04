// The hypermedia envelope types moved into the ABI (ADR 0737) so module controllers emit the exact wire
// shape core controllers do — one definition, no drift. These aliases keep every existing resource DTO
// reading as before; new code may use either spelling, they are the same types.
global using HypermediaResource = SimplArchive.ModuleAbi.HypermediaResource;
global using Link = SimplArchive.ModuleAbi.Link;
