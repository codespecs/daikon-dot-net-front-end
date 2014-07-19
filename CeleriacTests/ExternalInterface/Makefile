NAME := ExternalInterface

LIBS := OtherAssembly.dll

include ../Makefile.common

OtherAssembly.dll: 
	$(COMPILER) /out:OtherAssembly.dll /target:library .\\OtherAssembly\\*.cs 

