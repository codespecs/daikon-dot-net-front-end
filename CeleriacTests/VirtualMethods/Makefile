NAME := VirtualMethods

LIBS := ExternalProject.dll

include ../Makefile.common

DNFE_OPTIONS += --link-object-invariants --purity-file=VirtualMethods.pure

ExternalProject.dll: 
	$(COMPILER) /out:ExternalProject.dll /target:library .\\ExternalProject\\*.cs 