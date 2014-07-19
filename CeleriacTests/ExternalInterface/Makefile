NAME := ExternalInterface

LIBS := OtherAssembly.dll

include ../Makefile.common

DNFE_OPTIONS += --link-object-invariants --purity-file=ExternalInterface.pure

OtherAssembly.dll: 
	$(COMPILER) /out:OtherAssembly.dll /target:library .\\OtherAssembly\\*.cs 

