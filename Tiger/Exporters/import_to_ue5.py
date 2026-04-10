import unreal
import os
import json


class CharmImporter:
    def __init__(self, folder_path: str, b_unique_folder: bool) -> None:
        self.folder_path = folder_path
        script_name = os.path.basename(__file__)
        self.base_hash = script_name.split('_')[0]
        info_name = f"{self.base_hash}_info.cfg"
        self.config = json.load(open(os.path.join(self.folder_path, info_name)))
        if b_unique_folder:
            self.content_path = f"{self.config['UnrealInteropPath']}/{self.config['MeshName']}"
        else:
            self.content_path = f"{self.config['UnrealInteropPath']}"
        if not unreal.EditorAssetLibrary.does_directory_exist(self.content_path):
            unreal.EditorAssetLibrary.make_directory(self.content_path)

        # Load additional configs for terrain, entities, decorators, sky objects
        self.extra_configs = {}
        for suffix in ("Terrain", "Entities", "Decorators", "SkyObjects"):
            cfg_path = os.path.join(self.folder_path, f"{self.base_hash}_{suffix}_info.cfg")
            if os.path.exists(cfg_path):
                self.extra_configs[suffix] = json.load(open(cfg_path))

    def import_entity(self):
        self.make_materials()
        self.import_entity_mesh()
        self.assign_entity_materials()
        unreal.EditorAssetLibrary.save_directory(f"/Game/{self.content_path}/", False)

    def import_static(self):
        self.make_materials()
        self.import_static_mesh(combine=True)
        self.assign_static_materials()
        unreal.EditorAssetLibrary.save_directory(f"/Game/{self.content_path}/", False)

    def import_map(self):
        # Ensure map level exists before importing any assets
        self.ensure_map()

        # Statics
        self.make_materials()
        self.import_map_statics()
        self.assign_map_materials()

        # Terrain
        if "Terrain" in self.extra_configs:
            self.make_materials(self.extra_configs["Terrain"])
            self.import_map_fbx_dir("Terrain")
            self.assign_type_materials("Terrain")

        # Entities
        if "Entities" in self.extra_configs:
            self.make_materials(self.extra_configs["Entities"])
            self.import_map_fbx_dir("Entities")
            self.assign_type_materials("Entities")

        # Decorators
        if "Decorators" in self.extra_configs:
            self.make_materials(self.extra_configs["Decorators"])
            self.import_map_fbx_dir("Decorators")
            self.assign_type_materials("Decorators")

        # Sky Objects
        if "SkyObjects" in self.extra_configs:
            self.make_materials(self.extra_configs["SkyObjects"])
            self.import_map_fbx_dir("SkyObjects")
            self.assign_type_materials("SkyObjects")

        self.assemble_map()

        # Environment data from GlobalExporter (controlled by beta settings)
        if self.config.get("GenerateLights", False):
            self.import_lights()
        if self.config.get("GenerateSkybox", False):
            self.import_cubemaps()
        if self.config.get("GenerateAtmosphere", False):
            self.import_atmosphere()

        unreal.EditorAssetLibrary.save_directory(f"/Game/{self.content_path}/", False)

    def _set_complex_collision(self, asset_dir: str) -> None:
        """Set all static meshes in a directory to use complex collision as simple."""
        if not unreal.EditorAssetLibrary.does_directory_exist(asset_dir):
            return
        for asset_path in unreal.EditorAssetLibrary.list_assets(asset_dir, recursive=False):
            mesh = unreal.EditorAssetLibrary.load_asset(asset_path)
            if mesh is None or not isinstance(mesh, unreal.StaticMesh):
                continue
            body_setup = mesh.get_editor_property('body_setup')
            if body_setup is not None:
                body_setup.set_editor_property('collision_trace_flag', unreal.CollisionTraceFlag.CTF_USE_COMPLEX_AS_SIMPLE)

    def import_map_statics(self) -> None:
        """Import all individual static FBX files from Models/Statics/ directory."""
        import glob
        statics_dir = os.path.join(self.folder_path, "Models", "Statics")
        if not os.path.exists(statics_dir):
            # Fallback: try old single-file import
            self.import_static_mesh(combine=False)
            return

        fbx_files = glob.glob(os.path.join(statics_dir, "*.fbx"))
        if not fbx_files:
            print(f"[Charm] No FBX files found in {statics_dir}")
            return

        print(f"[Charm] Importing {len(fbx_files)} static meshes from Models/Statics/")
        tasks = []
        for fbx_path in fbx_files:
            task = self._make_static_import_task(fbx_path, f"/Game/{self.content_path}/Statics/")
            tasks.append(task)

        unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks(tasks)
        self._set_complex_collision(f"/Game/{self.content_path}/Statics/")

    def import_map_fbx_dir(self, type_name: str) -> None:
        """Import all FBX files from a Models/{type_name}/ subdirectory."""
        import glob
        model_dir = os.path.join(self.folder_path, "Models", type_name)
        if not os.path.exists(model_dir):
            print(f"[Charm] No Models/{type_name}/ directory found, skipping")
            return

        fbx_files = glob.glob(os.path.join(model_dir, "*.fbx"))
        if not fbx_files:
            print(f"[Charm] No FBX files found in Models/{type_name}/")
            return

        print(f"[Charm] Importing {len(fbx_files)} meshes from Models/{type_name}/")
        dest_path = f"/Game/{self.content_path}/{type_name}/"
        tasks = []
        for fbx_path in fbx_files:
            task = self._make_static_import_task(fbx_path, dest_path)
            tasks.append(task)

        unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks(tasks)
        self._set_complex_collision(dest_path)

    def _make_static_import_task(self, fbx_path: str, dest_path: str) -> unreal.AssetImportTask:
        """Create a static mesh import task."""
        task = unreal.AssetImportTask()
        task.set_editor_property("automated", True)
        task.set_editor_property("destination_path", dest_path)
        task.set_editor_property("filename", fbx_path)
        task.set_editor_property("replace_existing", True)
        task.set_editor_property("save", False)

        options = unreal.FbxImportUI()
        options.set_editor_property('import_mesh', True)
        options.set_editor_property('import_textures', False)
        options.set_editor_property('import_materials', False)
        options.set_editor_property('import_as_skeletal', False)
        options.static_mesh_import_data.set_editor_property('convert_scene', False)
        options.static_mesh_import_data.set_editor_property('import_uniform_scale', 100.0)
        options.static_mesh_import_data.set_editor_property('combine_meshes', True)
        options.static_mesh_import_data.set_editor_property('generate_lightmap_u_vs', False)
        options.static_mesh_import_data.set_editor_property('auto_generate_collision', False)
        options.static_mesh_import_data.set_editor_property('normal_import_method', unreal.FBXNormalImportMethod.FBXNIM_IMPORT_NORMALS)
        options.static_mesh_import_data.set_editor_property("vertex_color_import_option", unreal.VertexColorImportOption.REPLACE)
        options.static_mesh_import_data.set_editor_property("build_nanite", False)
        task.set_editor_property("options", options)
        return task

    def ensure_map(self) -> None:
        """Create the map level if it doesn't exist, or load it if it does."""
        map_path = f'/Game/{self.config["UnrealInteropPath"]}/map'
        if unreal.EditorAssetLibrary.does_asset_exist(map_path):
            unreal.EditorLevelLibrary.load_level(map_path)
        else:
            unreal.EditorLevelLibrary.new_level(map_path)
            # Add default scene actors based on environment generation settings
            if self.config.get("GenerateSkybox", False):
                unreal.EditorLevelLibrary.spawn_actor_from_class(unreal.SkyLight, location=[0, 0, 10000])
                unreal.EditorLevelLibrary.spawn_actor_from_class(unreal.SkyAtmosphere, location=[0, 0, 0])
            if self.config.get("GenerateLights", False):
                unreal.EditorLevelLibrary.spawn_actor_from_class(unreal.DirectionalLight, location=[0, 0, 10000], rotation=unreal.Rotator(-50, -30, 0))
            if self.config.get("GenerateFog", False):
                unreal.EditorLevelLibrary.spawn_actor_from_class(unreal.ExponentialHeightFog, location=[0, 0, 0])
            unreal.EditorLevelLibrary.save_current_level()

    def assemble_map(self) -> None:
        # Ensure map is loaded (may have been created by ensure_map or another script)
        self.ensure_map()

        # Place statics
        self._place_instances_from_config(self.config, "Statics")

        # Place terrain, entities, decorators, sky objects
        for type_name in ("Terrain", "Entities", "Decorators", "SkyObjects"):
            if type_name in self.extra_configs:
                self._place_instances_from_config(self.extra_configs[type_name], type_name)

        unreal.EditorLevelLibrary.save_current_level()

    def _place_instances_from_config(self, config: dict, type_name: str) -> None:
        """Place mesh instances in the level for a given asset type."""
        import re

        asset_dir = f'/Game/{self.content_path}/{type_name}/'
        if not unreal.EditorAssetLibrary.does_directory_exist(asset_dir):
            unreal.log_warning(f"[Charm] Asset directory {asset_dir} does not exist, skipping {type_name} placement")
            return

        instance_keys = set(config["Instances"].keys())

        # Build map of instance key -> list of UE asset paths
        asset_map = {}
        for x in unreal.EditorAssetLibrary.list_assets(asset_dir, recursive=False):
            # Skip non-mesh assets (PhysicsAsset, Skeleton, etc.)
            asset = unreal.EditorAssetLibrary.load_asset(x)
            if asset is None or not isinstance(asset, (unreal.StaticMesh, unreal.SkeletalMesh)):
                continue
            asset_name = x.split('/')[-1].split('.')[0]
            clean_name = re.sub(r'_ncl\d+_\d+$', '', asset_name)

            # Direct match
            name = clean_name if clean_name in instance_keys else None

            # For terrain/decorators: FBX files are {hash}_{group}.fbx, instance key is {hash}
            # Try stripping the last _N suffix to match the instance key
            if name is None:
                base = re.sub(r'_\d+$', '', clean_name)
                if base in instance_keys:
                    name = base

            # Fallback: try Parts config mapping
            if name is None:
                for mesh_hash, parts_data in config.get("Parts", {}).items():
                    if isinstance(parts_data, dict):
                        part_materials = parts_data.get("PartMaterials", parts_data)
                        for part_name in part_materials.keys():
                            base = re.sub(r'_Group\d+.*$', '', part_name)
                            if base == clean_name and mesh_hash in instance_keys:
                                name = mesh_hash
                                break
                    if name is not None:
                        break

            if name is None:
                name = clean_name

            if name not in asset_map:
                asset_map[name] = []
            asset_map[name].append(x)

        unreal.log_warning(f"[Charm] {type_name}: {len(asset_map)} unique meshes, {len(config['Instances'])} instance keys")

        for inst_key, instances in config["Instances"].items():
            if inst_key not in asset_map:
                unreal.log_warning(f"[Charm] {type_name}: No mesh match for instance key '{inst_key}'")
                continue
            parts = asset_map[inst_key]
            for part in parts:
                sm = unreal.EditorAssetLibrary.load_asset(part)
                if sm is None:
                    unreal.log_warning(f"[Charm] {type_name}: Failed to load asset '{part}'")
                    continue
                for instance in instances:
                    quat = unreal.Quat(instance["Rotation"][0], instance["Rotation"][1], instance["Rotation"][2], instance["Rotation"][3])
                    euler = quat.euler()
                    rotator = unreal.Rotator(-euler.x+180, -euler.y+180, -euler.z)
                    location = [-instance["Translation"][0]*100, instance["Translation"][1]*100, instance["Translation"][2]*100]
                    s = unreal.EditorLevelLibrary.spawn_actor_from_object(sm, location=location, rotation=rotator)
                    if s is None:
                        continue
                    # Scale can be either a scalar (1.3.2) or [x,y,z] array (2.4.7+)
                    scale = instance['Scale']
                    if isinstance(scale, list):
                        s.set_actor_label(s.get_actor_label() + f"_{scale[0]}")
                        s.set_actor_relative_scale3d(scale)
                    else:
                        s.set_actor_label(s.get_actor_label() + f"_{scale}")
                        s.set_actor_relative_scale3d([scale]*3)

    def assign_map_materials(self) -> None:
        self._assign_materials_in_dir(self.config, f'/Game/{self.content_path}/Statics/')

    def assign_type_materials(self, type_name: str) -> None:
        """Assign materials to meshes in a type-specific directory using the type's config."""
        cfg = self.extra_configs[type_name]
        asset_dir = f'/Game/{self.content_path}/{type_name}/'
        if unreal.EditorAssetLibrary.does_directory_exist(asset_dir):
            self._assign_materials_in_dir(cfg, asset_dir)

    def _assign_materials_in_dir(self, config: dict, asset_dir: str) -> None:
        """Assign materials to all meshes in a directory using the given config."""
        import re
        interop_path = config.get('UnrealInteropPath', self.config['UnrealInteropPath'])
        for x in unreal.EditorAssetLibrary.list_assets(asset_dir, recursive=False):
            mesh = unreal.load_asset(x)
            if mesh is None:
                continue
            # Skip non-mesh assets (e.g. PhysicsAsset, Skeleton)
            is_skeletal = isinstance(mesh, unreal.SkeletalMesh)
            is_static = isinstance(mesh, unreal.StaticMesh)
            if not is_skeletal and not is_static:
                continue
            mat_prop = "materials" if is_skeletal else "static_materials"
            mesh_materials = mesh.get_editor_property(mat_prop)
            new_mesh_materials = []
            for skeletal_material in mesh_materials:
                slot_name = skeletal_material.get_editor_property("material_slot_name").__str__()
                # Strip UE5 duplicate suffix (_ncl1_N)
                mat_hash = re.sub(r'_ncl\d+_\d+$', '', slot_name)
                mat_asset = unreal.load_asset(f"/Game/{interop_path}/Materials/M_{mat_hash}")
                if mat_asset:
                    skeletal_material.set_editor_property("material_interface", mat_asset)
                new_mesh_materials.append(skeletal_material)
            mesh.set_editor_property(mat_prop, new_mesh_materials)

    def assign_static_materials(self) -> None:
        import re
        mesh = unreal.load_asset(f"/Game/{self.content_path}/{self.config['MeshName']}")
        mesh_materials = mesh.get_editor_property("static_materials")
        new_mesh_materials = []
        for skeletal_material in mesh_materials:
            slot_name = skeletal_material.get_editor_property("material_slot_name").__str__()
            mat_hash = re.sub(r'_ncl\d+_\d+$', '', slot_name)
            mat_asset = unreal.load_asset(f"/Game/{self.config['UnrealInteropPath']}/Materials/M_{mat_hash}")
            if mat_asset:
                skeletal_material.set_editor_property("material_interface", mat_asset)
            new_mesh_materials.append(skeletal_material)
        mesh.set_editor_property("static_materials", new_mesh_materials)

    def assign_entity_materials(self) -> None:
        import re
        mesh = unreal.load_asset(f"/Game/{self.content_path}/{self.config['MeshName']}")
        mesh_materials = mesh.get_editor_property("materials")
        new_mesh_materials = []
        for skeletal_material in mesh_materials:
            slot_name = skeletal_material.get_editor_property("material_slot_name").__str__()
            mat_hash = re.sub(r'_ncl\d+_\d+$', '', slot_name)
            mat_asset = unreal.load_asset(f"/Game/{self.config['UnrealInteropPath']}/Materials/M_{mat_hash}")
            if mat_asset:
                skeletal_material.set_editor_property("material_interface", mat_asset)
            new_mesh_materials.append(skeletal_material)
        mesh.set_editor_property("materials", new_mesh_materials)

    def import_entity_mesh(self) -> None:
        task = unreal.AssetImportTask()
        task.set_editor_property("automated", True)
        task.set_editor_property("destination_path", f"/Game/{self.content_path}/")
        task.set_editor_property("filename", f"{self.folder_path}/{self.config['MeshName']}.fbx")
        task.set_editor_property("replace_existing", True)
        task.set_editor_property("save", True)

        options = unreal.FbxImportUI()
        options.set_editor_property('import_mesh', True)
        options.set_editor_property('import_textures', False)
        options.set_editor_property('import_materials', False)
        options.set_editor_property('import_as_skeletal', True)
        options.static_mesh_import_data.set_editor_property('convert_scene', False)
        options.static_mesh_import_data.set_editor_property('combine_meshes', False)
        options.static_mesh_import_data.set_editor_property('generate_lightmap_u_vs', False)
        options.static_mesh_import_data.set_editor_property('auto_generate_collision', False)
        options.static_mesh_import_data.set_editor_property("vertex_color_import_option", unreal.VertexColorImportOption.REPLACE)
        options.static_mesh_import_data.set_editor_property("build_nanite", False)
        task.set_editor_property("options", options)

        unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks([task])

    def import_static_mesh(self, combine) -> None:
        task = unreal.AssetImportTask()
        task.set_editor_property("automated", True)
        task.set_editor_property("destination_path", f"/Game/{self.content_path}/Statics/")
        task.set_editor_property("filename", f"{self.folder_path}/{self.config['MeshName']}.fbx")
        task.set_editor_property("replace_existing", True)
        task.set_editor_property("save", True)

        options = unreal.FbxImportUI()
        options.set_editor_property('import_mesh', True)
        options.set_editor_property('import_textures', False)
        options.set_editor_property('import_materials', False)
        options.set_editor_property('import_as_skeletal', False)
        options.static_mesh_import_data.set_editor_property('convert_scene', False)
        options.static_mesh_import_data.set_editor_property('import_uniform_scale', 100.0)
        options.static_mesh_import_data.set_editor_property('combine_meshes', combine)
        options.static_mesh_import_data.set_editor_property('generate_lightmap_u_vs', False)
        options.static_mesh_import_data.set_editor_property('auto_generate_collision', False)
        options.static_mesh_import_data.set_editor_property('normal_import_method', unreal.FBXNormalImportMethod.FBXNIM_IMPORT_NORMALS)
        options.static_mesh_import_data.set_editor_property("vertex_color_import_option", unreal.VertexColorImportOption.REPLACE)
        options.static_mesh_import_data.set_editor_property("build_nanite", False)
        task.set_editor_property("options", options)

        unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks([task])

    def _get_material_hashes(self, config=None) -> list:
        """Collect unique material hashes from Parts config."""
        if config is None:
            config = self.config
        hashes = set()
        for parts_data in config.get("Parts", {}).values():
            if isinstance(parts_data, dict):
                for mat_hash in parts_data.get("PartMaterials", {}).values():
                    if mat_hash:
                        hashes.add(mat_hash)
        return list(hashes)

    def _load_material_json(self, mat_hash: str) -> dict:
        """Load a material's JSON file from the Materials/ folder."""
        assets_path = self.config.get("AssetsPath", self.folder_path)
        mat_path = os.path.join(assets_path, "Materials", f"{mat_hash}.json")
        if not os.path.exists(mat_path):
            mat_path = os.path.join(self.folder_path, "Materials", f"{mat_hash}.json")
        if not os.path.exists(mat_path):
            return None
        return json.load(open(mat_path))

    def make_materials(self, config=None) -> None:
        if config is None:
            config = self.config
        interop_path = config.get('UnrealInteropPath', self.config['UnrealInteropPath'])
        materials = self._get_material_hashes(config)

        for mat in materials:
            mat_path = f"/Game/{interop_path}/Materials/M_{mat}"
            # Skip if material already exists
            if unreal.EditorAssetLibrary.does_asset_exist(mat_path):
                continue
            try:
                material = self.make_material(mat, config)
                if material is not None:
                    unreal.MaterialEditingLibrary.recompile_material(material)
            except Exception as e:
                print(f"[Charm] Failed to create material {mat}: {e}")

    def make_material(self, matstr: str, config=None) -> unreal.Material:
        if config is None:
            config = self.config
        interop_path = config.get('UnrealInteropPath', self.config['UnrealInteropPath'])

        mat_json = self._load_material_json(matstr)
        if mat_json is None:
            print(f"[Charm] No material JSON found for {matstr}, skipping")
            return None

        # Make base material
        material = unreal.AssetToolsHelpers.get_asset_tools().create_asset("M_" + matstr, f"/Game/{interop_path}/Materials", unreal.Material, unreal.MaterialFactoryNew())

        usf_path = f"{self.folder_path}/Shaders/Unreal/PS_{matstr}.usf"
        if os.path.exists(usf_path):
            # Add textures
            texture_samples = self.add_textures(material, matstr, mat_json)

            # Add custom node
            custom_node = self.add_custom_node(material, texture_samples, matstr, mat_json)

            # Detect transparent for output wiring
            with open(usf_path, "r") as f:
                is_transparent = "// transparent" in f.read()

            # Set output, not using in-built custom expression system because I want to leave it open for manual control
            self.create_output(material, custom_node, is_transparent)
        else:
            material.set_editor_property("blend_mode", unreal.BlendMode.BLEND_MASKED)
            const = unreal.MaterialEditingLibrary.create_material_expression(material, unreal.MaterialExpressionConstant, -300, 0)
            unreal.MaterialEditingLibrary.connect_material_property(const, "", unreal.MaterialProperty.MP_OPACITY_MASK)

        return material

    def create_output(self, material: unreal.Material, custom_node: unreal.MaterialExpressionCustom, is_transparent: bool = False) -> None:
        mat_att = unreal.MaterialEditingLibrary.create_material_expression(material, unreal.MaterialExpressionBreakMaterialAttributes, -300, 0)
        # Connect custom node to the new break
        unreal.MaterialEditingLibrary.connect_material_expressions(custom_node, '', mat_att, 'Attr')
        # Connect all outputs
        unreal.MaterialEditingLibrary.connect_material_property(mat_att, "BaseColor", unreal.MaterialProperty.MP_BASE_COLOR)
        unreal.MaterialEditingLibrary.connect_material_property(mat_att, "Metallic", unreal.MaterialProperty.MP_METALLIC)
        unreal.MaterialEditingLibrary.connect_material_property(mat_att, "Roughness", unreal.MaterialProperty.MP_ROUGHNESS)
        unreal.MaterialEditingLibrary.connect_material_property(mat_att, "EmissiveColor", unreal.MaterialProperty.MP_EMISSIVE_COLOR)
        if is_transparent:
            unreal.MaterialEditingLibrary.connect_material_property(mat_att, "Opacity", unreal.MaterialProperty.MP_OPACITY)
        else:
            unreal.MaterialEditingLibrary.connect_material_property(mat_att, "OpacityMask", unreal.MaterialProperty.MP_OPACITY_MASK)
        unreal.MaterialEditingLibrary.connect_material_property(mat_att, "Normal", unreal.MaterialProperty.MP_NORMAL)
        unreal.MaterialEditingLibrary.connect_material_property(mat_att, "AmbientOcclusion", unreal.MaterialProperty.MP_AMBIENT_OCCLUSION)

    def add_custom_node(self, material: unreal.Material, texture_nodes: list, matstr: str, mat_json: dict) -> unreal.MaterialExpressionCustom:
        import re as _re

        ps_textures = mat_json.get("Material", {}).get("Pixel", {}).get("Textures", {})
        all_cfg_indices = sorted([int(x) for x in ps_textures.keys()])
        # Filter to 2D-only material texture indices (matches V2 ClassifyTextures)
        all_2d_indices = [int(x) for x, t in ps_textures.items() if t.get('Dimension', '2D') == '2D']
        all_2d_indices.sort()

        custom_node = unreal.MaterialEditingLibrary.create_material_expression(material, unreal.MaterialExpressionCustom, -500, 0)

        code = open(f"{self.folder_path}/Shaders/Unreal/PS_{matstr}.usf", "r").read()

        is_transparent = "// transparent" in code
        # is_v2 = "Texture2DSampleLevel(" in code and "Material_Texture2D_" not in code

        if is_transparent:
            material.set_editor_property("blend_mode", unreal.BlendMode.BLEND_TRANSLUCENT)
            material.set_editor_property("two_sided", True)
        elif "// masked" in code:
            material.set_editor_property("blend_mode", unreal.BlendMode.BLEND_MASKED)
            material.set_editor_property("two_sided", True)

        # V2: scan for Texture2DSampleLevel(tN, ...) to find texture input names
        tex_input_names = sorted(set(_re.findall(r'Texture2DSampleLevel\((t\d+),', code)))
        n_texture_inputs = len(tex_input_names)
        # V1 DISABLED — all shaders now go through V2
        # if is_v2:
        #     tex_input_names = sorted(set(_re.findall(r'Texture2DSampleLevel\((t\d+),', code)))
        #     n_texture_inputs = len(tex_input_names)
        # else:
        #     used_positions = sorted(set(int(m.group(1)) for m in _re.finditer(r"Material_Texture2D_(\d+)(?:\.|Sampler)", code)))
        #     n_texture_inputs = (max(used_positions) + 1) if used_positions else 0
        #     tex_input_names = [f't{i}' for i in range(n_texture_inputs)]

        inputs = []
        for name in tex_input_names:
            ci = unreal.CustomInput()
            ci.set_editor_property('input_name', name)
            inputs.append(ci)
        if is_transparent:
            sp = unreal.CustomInput()
            sp.set_editor_property('input_name', 'screenPos')
            inputs.append(sp)
            tss = unreal.CustomInput()
            tss.set_editor_property('input_name', 'twoSidedSign')
            inputs.append(tss)
        ci = unreal.CustomInput()
        ci.set_editor_property('input_name', 'tx')
        inputs.append(ci)
        vc = unreal.CustomInput()
        vc.set_editor_property('input_name', 'vc')
        inputs.append(vc)
        vcw = unreal.CustomInput()
        vcw.set_editor_property('input_name', 'vcw')
        inputs.append(vcw)
        viewdir = unreal.CustomInput()
        viewdir.set_editor_property('input_name', 'viewDir')
        inputs.append(viewdir)

        custom_node.set_editor_property('code', code)
        custom_node.set_editor_property('inputs', inputs)
        custom_node.set_editor_property('output_type', unreal.CustomMaterialOutputType.CMOT_MATERIAL_ATTRIBUTES)

        # V2: connect TextureObjectParameter nodes using sequential mapping
        # The V2 converter assigns t0, t1, t2... to 2D material textures in order
        for seq, orig_idx in enumerate(all_2d_indices):
            input_name = f't{seq}'
            if input_name not in tex_input_names:
                break
            if orig_idx in texture_nodes:
                print(f"  V2: connecting texture {orig_idx} -> {input_name} (type: {type(texture_nodes[orig_idx]).__name__})")
                unreal.MaterialEditingLibrary.connect_material_expressions(
                    texture_nodes[orig_idx], '', custom_node, input_name)
        # V1 DISABLED — all shaders now go through V2
        # else:
        #     default_tex = unreal.load_asset('/Engine/EngineMaterials/DefaultDiffuse')
        #     for seq in range(n_texture_inputs):
        #         if seq < len(all_cfg_indices):
        #             orig = all_cfg_indices[seq]
        #             if orig in texture_nodes:
        #                 unreal.MaterialEditingLibrary.connect_material_expressions(
        #                     texture_nodes[orig], 'RGBA', custom_node, f't{seq}')
        #                 continue
        #         placeholder = unreal.MaterialEditingLibrary.create_material_expression(
        #             material, unreal.MaterialExpressionTextureSample, -1000, -500 + 250 * seq)
        #         if default_tex:
        #             placeholder.set_editor_property('texture', default_tex)
        #         unreal.MaterialEditingLibrary.connect_material_expressions(
        #             placeholder, 'RGBA', custom_node, f't{seq}')

        texcoord = unreal.MaterialEditingLibrary.create_material_expression(material, unreal.MaterialExpressionTextureCoordinate, -500, 400)
        unreal.MaterialEditingLibrary.connect_material_expressions(texcoord, '', custom_node, 'tx')

        vertex_color = unreal.MaterialEditingLibrary.create_material_expression(material, unreal.MaterialExpressionVertexColor, -500, 500)
        unreal.MaterialEditingLibrary.connect_material_expressions(vertex_color, '', custom_node, 'vc')
        unreal.MaterialEditingLibrary.connect_material_expressions(vertex_color, 'A', custom_node, 'vcw')

        cam_vec = unreal.MaterialEditingLibrary.create_material_expression(material, unreal.MaterialExpressionCameraVectorWS, -500, 700)
        vec_transform = unreal.MaterialEditingLibrary.create_material_expression(material, unreal.MaterialExpressionTransform, -500, 800)
        vec_transform.set_editor_property('transform_source_type', unreal.MaterialVectorCoordTransformSource.TRANSFORMSOURCE_WORLD)
        vec_transform.set_editor_property('transform_type', unreal.MaterialVectorCoordTransform.TRANSFORM_TANGENT)
        unreal.MaterialEditingLibrary.connect_material_expressions(cam_vec, '', vec_transform, '')
        unreal.MaterialEditingLibrary.connect_material_expressions(vec_transform, '', custom_node, 'viewDir')

        if is_transparent:
            screen_pos = unreal.MaterialEditingLibrary.create_material_expression(material, unreal.MaterialExpressionScreenPosition, -500, 900)
            unreal.MaterialEditingLibrary.connect_material_expressions(screen_pos, '', custom_node, 'screenPos')

            two_sided = unreal.MaterialEditingLibrary.create_material_expression(material, unreal.MaterialExpressionTwoSidedSign, -500, 1000)
            unreal.MaterialEditingLibrary.connect_material_expressions(two_sided, '', custom_node, 'twoSidedSign')

        return custom_node

    def add_textures(self, material: unreal.Material, matstr: str, mat_json: dict) -> dict:
        texture_nodes = {}

        ps_textures = mat_json.get("Material", {}).get("Pixel", {}).get("Textures", {})

        # Import texture list for the material
        tex_factory = unreal.TextureFactory()
        tex_factory.set_editor_property('supported_class', unreal.Texture2D)
        names = [f"{self.folder_path}/Textures/{texstruct['Hash']}.dds" for i, texstruct in ps_textures.items()]
        srgbs = {int(i): texstruct.get('Colorspace', '') in ('sRGB', 'Srgb') for i, texstruct in ps_textures.items()}
        import_tasks = []
        for name in names:
            asset_import_task = unreal.AssetImportTask()
            asset_import_task.set_editor_property('filename', name)
            asset_import_task.set_editor_property('destination_path', f'/Game/{self.content_path}/Textures')
            asset_import_task.set_editor_property('save', True)
            asset_import_task.set_editor_property('replace_existing', False)
            asset_import_task.set_editor_property('automated', True)
            import_tasks.append(asset_import_task)

        unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks(import_tasks)

        # V2: create TextureObjectParameter nodes for all 2D material textures
        for i, texstruct in ps_textures.items():
            i = int(i)
            dim = texstruct.get('Dimension', '2D')

            # Only 2D textures are supported as custom expression inputs for now
            if dim != '2D':
                continue

            tex_node = unreal.MaterialEditingLibrary.create_material_expression(
                material, unreal.MaterialExpressionTextureObjectParameter, -1000, -500 + 250 * i)
            tex_node.set_editor_property('parameter_name', f'tex_{texstruct["Hash"]}')
            # V1 DISABLED — all shaders now go through V2
            # tex_node = unreal.MaterialEditingLibrary.create_material_expression(
            #     material, unreal.MaterialExpressionTextureSample, -1000, -500 + 250 * i)

            ts_TextureUePath = f"/Game/{self.content_path}/Textures/{texstruct['Hash']}.{texstruct['Hash']}"
            ts_LoadedTexture = unreal.EditorAssetLibrary.load_asset(ts_TextureUePath)
            if not ts_LoadedTexture:
                continue
            ts_LoadedTexture.set_editor_property('srgb', srgbs.get(i, False))
            if srgbs.get(i, False):
                ts_LoadedTexture.set_editor_property('compression_settings', unreal.TextureCompressionSettings.TC_DEFAULT)
            else:
                ts_LoadedTexture.set_editor_property('compression_settings', unreal.TextureCompressionSettings.TC_VECTOR_DISPLACEMENTMAP)

            tex_node.set_editor_property('texture', ts_LoadedTexture)

            # V1 DISABLED — sampler type only needed for TextureSample nodes
            # if not is_v2:
            #     actual_compression = ts_LoadedTexture.get_editor_property('compression_settings')
            #     actual_srgb = ts_LoadedTexture.get_editor_property('srgb')
            #     if actual_compression == unreal.TextureCompressionSettings.TC_NORMALMAP:
            #         tex_node.set_editor_property("sampler_type", unreal.MaterialSamplerType.SAMPLERTYPE_NORMAL)
            #     elif actual_srgb:
            #         tex_node.set_editor_property("sampler_type", unreal.MaterialSamplerType.SAMPLERTYPE_COLOR)
            #     else:
            #         tex_node.set_editor_property("sampler_type", unreal.MaterialSamplerType.SAMPLERTYPE_LINEAR_COLOR)

            texture_nodes[i] = tex_node
            unreal.EditorAssetLibrary.save_loaded_asset(ts_LoadedTexture)

        return texture_nodes

    def import_lights(self) -> None:
        """Spawn light actors from exported light data."""
        lights_path = os.path.join(self.folder_path, "Rendering", "Lights.json")
        if not os.path.exists(lights_path):
            return

        with open(lights_path) as f:
            lights = json.load(f)

        light_type_map = {
            "Point": unreal.PointLight,
            "Spot": unreal.SpotLight,
            "Area": unreal.RectLight,
        }

        count = 0
        for light_key, light_data in lights.items():
            light_class = light_type_map.get(light_data.get("Type"), unreal.PointLight)
            color = light_data.get("Color", [1, 1, 1, 1])
            attenuation = light_data.get("Attenuation", 10.0)

            for i, inst in enumerate(light_data.get("Instances", [])):
                loc = inst["Translation"]
                location = [-loc[0] * 100, loc[1] * 100, loc[2] * 100]
                rot = inst.get("Rotation", [0, 0, 0, 1])
                quat = unreal.Quat(rot[0], rot[1], rot[2], rot[3])
                euler = quat.euler()
                rotator = unreal.Rotator(-euler.x + 180, -euler.y + 180, -euler.z)

                actor = unreal.EditorLevelLibrary.spawn_actor_from_class(light_class, location=location, rotation=rotator)
                if actor is None:
                    continue
                actor.set_actor_label(f"D2_{light_data.get('Type', 'Light')}_{light_key}_{i}")

                component = actor.light_component
                component.set_editor_property('light_color', unreal.LinearColor(color[0], color[1], color[2], color[3]))
                component.set_editor_property('attenuation_radius', attenuation * 100)

                scale = inst.get("Scale", [1, 1, 1])
                actor.set_actor_relative_scale3d(scale)
                count += 1

        print(f"[Charm] Placed {count} lights from {len(lights)} light groups")

    def import_cubemaps(self) -> None:
        """Spawn reflection captures from exported cubemap data."""
        cubemap_path = os.path.join(self.folder_path, "Rendering", "Cubemaps.json")
        if not os.path.exists(cubemap_path):
            return

        with open(cubemap_path) as f:
            cubemaps = json.load(f)

        # Import cubemap textures
        tex_folder = os.path.join(self.folder_path, "Textures", "Cubemaps")
        if os.path.exists(tex_folder):
            import glob
            tex_files = glob.glob(os.path.join(tex_folder, "*.dds"))
            if tex_files:
                tasks = []
                for tex_file in tex_files:
                    task = unreal.AssetImportTask()
                    task.set_editor_property('filename', tex_file)
                    task.set_editor_property('destination_path', f'/Game/{self.content_path}/Textures/Cubemaps')
                    task.set_editor_property('save', True)
                    task.set_editor_property('replace_existing', False)
                    task.set_editor_property('automated', True)
                    tasks.append(task)
                unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks(tasks)

        count = 0
        for name, data in cubemaps.items():
            transform = data.get("Transform", {})
            shape = data.get("CubemapShape", "Sphere")
            loc = transform.get("Translation", [0, 0, 0])
            location = [-loc[0] * 100, loc[1] * 100, loc[2] * 100]

            if shape == "Box":
                actor = unreal.EditorLevelLibrary.spawn_actor_from_class(unreal.BoxReflectionCapture, location=location)
            else:
                actor = unreal.EditorLevelLibrary.spawn_actor_from_class(unreal.SphereReflectionCapture, location=location)

            if actor is None:
                continue

            actor.set_actor_label(f"D2_Cubemap_{name}")

            rot = transform.get("Rotation", [0, 0, 0, 1])
            quat = unreal.Quat(rot[0], rot[1], rot[2], rot[3])
            euler = quat.euler()
            actor.set_actor_rotation(unreal.Rotator(-euler.x + 180, -euler.y + 180, -euler.z), False)

            scale = transform.get("Scale", [1, 1, 1])
            actor.set_actor_relative_scale3d(scale)
            count += 1

        print(f"[Charm] Placed {count} reflection captures from cubemap data")

    def import_atmosphere(self) -> None:
        """Import atmosphere LUT textures and set sun direction from day cycle data."""
        atmo_path = os.path.join(self.folder_path, "Rendering", "Atmosphere.json")
        if not os.path.exists(atmo_path):
            return

        with open(atmo_path) as f:
            atmo = json.load(f)

        # Import atmosphere LUT textures as reference
        tex_folder = os.path.join(self.folder_path, "Textures", "Atmosphere")
        if os.path.exists(tex_folder):
            import glob
            tex_files = glob.glob(os.path.join(tex_folder, "*.dds"))
            if tex_files:
                tasks = []
                for tex_file in tex_files:
                    task = unreal.AssetImportTask()
                    task.set_editor_property('filename', tex_file)
                    task.set_editor_property('destination_path', f'/Game/{self.content_path}/Textures/Atmosphere')
                    task.set_editor_property('save', True)
                    task.set_editor_property('replace_existing', False)
                    task.set_editor_property('automated', True)
                    tasks.append(task)
                unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks(tasks)
                print(f"[Charm] Imported {len(tex_files)} atmosphere LUT textures as reference")

        # Set sun direction from day cycle first rotation keyframe
        day_cycle = atmo.get("DayCycle")
        if day_cycle and day_cycle.get("DayCycleRotations"):
            rotations = day_cycle["DayCycleRotations"]
            first_rot = None
            for rot_data in rotations:
                rots = rot_data.get("Rotations", [])
                if rots:
                    first_rot = rots[0]
                    break

            if first_rot:
                # Vector4 serializes as {"X":..., "Y":..., "Z":..., "W":...} from C#
                if isinstance(first_rot, dict):
                    qx, qy, qz, qw = first_rot.get("X", 0), first_rot.get("Y", 0), first_rot.get("Z", 0), first_rot.get("W", 1)
                else:
                    qx, qy, qz, qw = first_rot[0], first_rot[1], first_rot[2], first_rot[3]

                quat = unreal.Quat(qx, qy, qz, qw)
                euler = quat.euler()
                sun_rotator = unreal.Rotator(-euler.x + 180, -euler.y + 180, -euler.z)

                # Find existing DirectionalLight (created by ensure_map) and update its rotation
                actors = unreal.EditorLevelLibrary.get_all_level_actors()
                for actor in actors:
                    if isinstance(actor, unreal.DirectionalLight):
                        actor.set_actor_rotation(sun_rotator, False)
                        actor.set_actor_label("D2_Sun")
                        print(f"[Charm] Set sun direction from day cycle (cycle: {day_cycle.get('Seconds', 0)}s)")
                        break

        print("[Charm] Atmosphere data imported (LUT textures are for manual reference)")

    """
    Updates all materials used by this model to the latest .usfs found in the Shaders/ folder.
    Very useful for improving the material quality without much manual work.
    """
    def update_material_code(self) -> None:
        # Get all materials to update
        materials = self._get_material_hashes()

        # For each material, find the code node and update it
        mats = {unreal.EditorAssetLibrary.load_asset(f"/Game/{self.config['UnrealInteropPath']}/Materials/M_{matstr}"): matstr for matstr in materials}
        it = unreal.ObjectIterator()
        for x in it:
            if x.get_outer() in mats:
                if isinstance(x, unreal.MaterialExpressionCustom):
                    code = open(f"{self.folder_path}/Shaders/Unreal/PS_{mats[x.get_outer()]}.usf", "r").read()
                    x.set_editor_property('code', code)
                    print(f"Updated material {mats[x.get_outer()]}")

        unreal.EditorAssetLibrary.save_directory(f"/Game/{self.content_path}/Materials/", False)


if __name__ == "__main__":
    importer = CharmImporter(os.path.dirname(os.path.realpath(__file__)), b_unique_folder=False)
    importer.import_entity()
    # importer.update_material_code()
