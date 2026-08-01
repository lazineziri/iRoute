#!/bin/sh
set -eu
mkdir -p target/conformance-classes
javac --release 25 -Xlint:all -Werror -d target/conformance-classes \
  src/main/java/dev/iroute/IRouteClient.java \
  src/test/java/dev/iroute/SdkConformanceTest.java
java -cp target/conformance-classes dev.iroute.SdkConformanceTest
